"""Validate the exact embedded benchmark programs and Python oracle without .NET or a model."""
import argparse
import json
import pathlib
import re
import subprocess
import sys
import textwrap


def sources(task, reference):
    files = {"implementation.py": task["referenceSource" if reference else "brokenSource"],
             "README.md": task["requirement"] + "\n"}
    for item in task.get("projectFiles", []):
        if item["path"] in files:
            raise AssertionError("Duplicate fixture path: " + item["path"])
        files[item["path"]] = item["referenceSource" if reference else "brokenSource"]
    public = "import unittest\nfrom implementation import solve\n\nclass ContractTests(unittest.TestCase):\n"
    for index, case in enumerate(task["cases"][:2]):
        public += (f"    def test_example_{index}(self):\n        import json\n"
                   f"        args = json.loads({json.dumps(case['arguments'], ensure_ascii=False)!r})\n"
                   f"        expected = json.loads({json.dumps(case['expected'], ensure_ascii=False)!r})\n"
                   "        self.assertEqual(solve(*args), expected)\n")
    files["test_implementation.py"] = public
    return files


def materialize(directory, task, reference, template, revert=None):
    directory.mkdir()
    workspace = directory / "workspace"
    workspace.mkdir()
    files = sources(task, reference)
    if revert:
        files[revert] = sources(task, False)[revert]
    for relative, content in files.items():
        path = pathlib.PurePosixPath(relative)
        assert not path.is_absolute() and ".." not in path.parts and "\\" not in relative and ":" not in relative
        target = workspace / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")
    oracle = template.replace("CASES", repr(json.dumps(task["cases"], ensure_ascii=False)))
    oracle = oracle.replace("PROBES", repr(json.dumps(task.get("moduleProbes", []), ensure_ascii=False)))
    (directory / "independent_oracle.py").write_text(oracle, encoding="utf-8", newline="\n")
    return workspace


def execute_oracle(directory, workspace):
    result = subprocess.run([sys.executable, "-B", str(directory / "independent_oracle.py"), str(workspace)],
                            cwd=workspace, capture_output=True, text=True, encoding="utf-8", timeout=30,
                            env={**__import__("os").environ, "PYTHONIOENCODING": "utf-8"})
    try:
        receipt = json.loads(result.stdout)
    except json.JSONDecodeError:
        raise AssertionError(f"Oracle did not produce a valid receipt: {result.stdout} {result.stderr}") from None
    assert (result.returncode == 0) == receipt["passed"]
    return {"exitCode": result.returncode, **receipt}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    options = parser.parse_args()
    output = options.output
    assert output.is_absolute() and not output.exists(), "Use a fresh absolute output directory."
    output.mkdir(parents=True)
    source = pathlib.Path(__file__).with_name("BenchmarkTasks.cs").read_text(encoding="utf-8")
    definition = re.search(r'private const string Definitions = """\n(.*?)\n        """;', source, re.S)
    template = re.search(r'internal string Oracle => """\n(.*?)\n        """', source, re.S)
    assert definition and template, "Embedded fixture/oracle source anchors are missing."
    tasks = json.loads(textwrap.dedent(definition.group(1)))
    oracle_template = textwrap.dedent(template.group(1))
    assert len(tasks) == 12 and len({task["id"] for task in tasks}) == 12
    results = []
    for task in tasks:
        before_dir = output / (task["id"] + "-broken")
        after_dir = output / (task["id"] + "-reference")
        before_workspace = materialize(before_dir, task, False, oracle_template)
        after_workspace = materialize(after_dir, task, True, oracle_template)
        before = execute_oracle(before_dir, before_workspace)
        after = execute_oracle(after_dir, after_workspace)
        assert not before["passed"], task["id"] + ": broken implementation unexpectedly passed"
        assert after["passed"], task["id"] + ": reference failed: " + json.dumps(after)
        public = subprocess.run([sys.executable, "-B", "-m", "unittest", "discover", "-v"], cwd=after_workspace,
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        assert public.returncode == 0, task["id"] + ": reference public tests failed: " + public.stderr
        mutable = [item["path"] for item in task.get("projectFiles", []) if item.get("mutable", True)] or ["implementation.py"]
        hybrids = []
        for index, relative in enumerate(mutable):
            mixed_dir = output / (task["id"] + "-restore-bug-" + str(index))
            workspace = materialize(mixed_dir, task, True, oracle_template, revert=relative)
            check = execute_oracle(mixed_dir, workspace)
            assert not check["passed"], task["id"] + ": claimed necessary file change is not necessary: " + relative
            hybrids.append({"path": relative, "brokenModuleRejected": True, "failures": len(check["failures"])})
        diagnostic = None
        if task.get("requiresOutputRead"):
            marker = task["diagnosticMarker"]
            run = subprocess.run([sys.executable, "-B", "diagnose.py"], cwd=before_workspace,
                                 capture_output=True, text=True, encoding="utf-8", timeout=30)
            position = run.stdout.index(marker)
            assert run.returncode == 1 and position > 12000 and len(run.stdout) - position > 12000
            fixed_run = subprocess.run([sys.executable, "-B", "diagnose.py"], cwd=after_workspace,
                                       capture_output=True, text=True, encoding="utf-8", timeout=30)
            assert fixed_run.returncode == 0 and marker in fixed_run.stdout
            diagnostic = {"brokenExitCode": run.returncode, "referenceExitCode": fixed_run.returncode,
                          "stdoutCharacters": len(run.stdout), "markerOffset": position, "marker": marker,
                          "stderrCharacters": len(run.stderr)}
        result = {"task": task["id"], "brokenRejected": True, "referencePassed": True, "publicTestsPassed": True,
                  "oracleCases": after["cases"], "requiredChanges": hybrids,
                  "requiresSearch": task.get("requiresSearch", False), "diagnostic": diagnostic}
        results.append(result)
        (output / "fixture-checks.json").write_text(json.dumps({"modelCalls": 0, "dotnetRuns": 0, "tasks": results},
                                                             ensure_ascii=False, indent=2), encoding="utf-8")
        print(task["id"] + ": broken rejected, reference/public tests passed; every required module change verified", flush=True)
    print("12/12 exact embedded fixtures passed; no .NET build or model request.")


if __name__ == "__main__":
    main()

