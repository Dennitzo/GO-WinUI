using System.Text.Json;

namespace GoAi.CodingBenchmarks;

internal sealed record BenchmarkCase(JsonElement Arguments, JsonElement Expected, string ExpectedType);
internal sealed record BenchmarkSourceFile(string Path, string BrokenSource, string ReferenceSource, bool Mutable = true);
internal sealed record BenchmarkModuleProbe(string Module, string Member, IReadOnlyList<BenchmarkCase> Cases, bool Callable = true);

internal sealed record BenchmarkTask(string Id, string Requirement, string BrokenSource, string ReferenceSource,
    IReadOnlyList<BenchmarkCase> Cases, bool Research = false, IReadOnlyList<BenchmarkSourceFile>? ProjectFiles = null,
    bool RequiresSearch = false, bool RequiresOutputRead = false, string? DiagnosticMarker = null,
    IReadOnlyList<BenchmarkModuleProbe>? ModuleProbes = null)
{
    internal IReadOnlyList<string> MutablePaths => ProjectFiles is { Count: > 0 }
        ? ProjectFiles.Where(static file => file.Mutable).Select(static file => file.Path).ToArray() : ["implementation.py"];
    internal IReadOnlyList<string> RequiredChangedPaths => MutablePaths;
    internal IReadOnlyDictionary<string, string> ExpectedFiles => GetFiles(reference: false);

    internal IReadOnlyDictionary<string, string> GetFiles(bool reference)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["implementation.py"] = reference ? ReferenceSource : BrokenSource,
            ["test_implementation.py"] = PublicTests,
            ["README.md"] = Requirement + "\n",
        };
        foreach (var file in ProjectFiles ?? [])
            if (!files.TryAdd(file.Path, reference ? file.ReferenceSource : file.BrokenSource))
                throw new InvalidDataException("Duplicate benchmark fixture path: " + file.Path);
        return files;
    }

    internal string PublicTests => "import unittest\nfrom implementation import solve\n\nclass ContractTests(unittest.TestCase):\n"
        + string.Concat(Cases.Take(2).Select((item, index) =>
            $"    def test_example_{index}(self):\n        import json\n        args = json.loads({PythonString(item.Arguments.GetRawText())})\n        expected = json.loads({PythonString(item.Expected.GetRawText())})\n        self.assertEqual(solve(*args), expected)\n"));

    internal string Oracle => """
        import copy, importlib, importlib.util, json, pathlib, sys
        workspace = pathlib.Path(sys.argv[1]).resolve()
        sys.path.insert(0, str(workspace))
        spec = importlib.util.spec_from_file_location('bench_subject', workspace / 'implementation.py')
        subject = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(subject)
        cases = json.loads(CASES)
        probes = json.loads(PROBES)
        failures = []
        checked = 0
        def check(target, items, scope, callable_target=True):
            global checked
            for index, case in enumerate(items):
                checked += 1
                try:
                    arguments = copy.deepcopy(case['arguments'])
                    original_args = copy.deepcopy(arguments)
                    actual = target(*arguments) if callable_target else target
                    expected = case['expected']
                    if actual != expected or type(actual).__name__ != case['expectedType'] or arguments != original_args:
                        failures.append({'scope': scope, 'case': index, 'expected': expected, 'actual': repr(actual)})
                except Exception as error:
                    failures.append({'scope': scope, 'case': index, 'error': type(error).__name__ + ': ' + str(error)})
        check(subject.solve, cases, 'public-api')
        for probe in probes:
            try:
                target = getattr(importlib.import_module(probe['module']), probe['member'])
                check(target, probe['cases'], probe['module'] + '.' + probe['member'], probe.get('callable', True))
            except Exception as error:
                failures.append({'scope': probe['module'], 'error': type(error).__name__ + ': ' + str(error)})
        print(json.dumps({'oracle': 'independent-python-contract-v2', 'cases': checked, 'passed': not failures, 'failures': failures}, ensure_ascii=False))
        sys.exit(1 if failures else 0)
        """.Replace("CASES", PythonString(JsonSerializer.Serialize(Cases, JsonSerializerOptions.Web)), StringComparison.Ordinal)
        .Replace("PROBES", PythonString(JsonSerializer.Serialize(ModuleProbes ?? [], JsonSerializerOptions.Web)), StringComparison.Ordinal);

    internal static string PythonString(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "'";
}

internal static class BenchmarkTasks
{
    internal const string ResearchUrl = "https://docs.python.org/3.13/library/urllib.parse.html";
    internal static readonly IReadOnlyList<BenchmarkTask> All =
        JsonSerializer.Deserialize<BenchmarkTask[]>(Definitions, JsonSerializerOptions.Web)
        ?? throw new InvalidDataException("Benchmark fixture definitions are missing.");

    // Versioned source data keeps the Python-only fixture preflight on the exact
    // same source programs and independently specified contracts as the .NET runner.
    private const string Definitions = """
        [
          {
            "id": "01-add",
            "requirement": "solve(a, b) addiert zwei ganze Zahlen, einschließlich negativer Werte und Null.",
            "brokenSource": "def solve(a, b):\n    return a - b\n",
            "referenceSource": "def solve(a, b):\n    return a + b\n",
            "cases": [
              {
                "arguments": [
                  2,
                  3
                ],
                "expected": 5,
                "expectedType": "int"
              },
              {
                "arguments": [
                  -2,
                  -3
                ],
                "expected": -5,
                "expectedType": "int"
              },
              {
                "arguments": [
                  0,
                  0
                ],
                "expected": 0,
                "expectedType": "int"
              },
              {
                "arguments": [
                  71,
                  -19
                ],
                "expected": 52,
                "expectedType": "int"
              },
              {
                "arguments": [
                  -900,
                  900
                ],
                "expected": 0,
                "expectedType": "int"
              }
            ]
          },
          {
            "id": "02-average",
            "requirement": "Die öffentliche Funktion solve(values) soll den arithmetischen Mittelwert als float liefern, für die leere Liste 0.0. Suche zuerst mit coding.search nach dem tatsächlich aktiven Berechnungspfad: Im Projekt liegen aktive und archivierte Statistikmodule. Erhalte die öffentliche API und korrigiere nur das aktive Statistikmodul.",
            "brokenSource": "from analytics.api import solve\n",
            "referenceSource": "from analytics.api import solve\n",
            "cases": [
              {
                "arguments": [
                  [
                    2,
                    4
                  ]
                ],
                "expected": 3,
                "expectedType": "float"
              },
              {
                "arguments": [
                  []
                ],
                "expected": 0,
                "expectedType": "float"
              },
              {
                "arguments": [
                  [
                    -8,
                    2,
                    3
                  ]
                ],
                "expected": -1,
                "expectedType": "float"
              },
              {
                "arguments": [
                  [
                    7
                  ]
                ],
                "expected": 7,
                "expectedType": "float"
              },
              {
                "arguments": [
                  [
                    0,
                    1
                  ]
                ],
                "expected": 0.5,
                "expectedType": "float"
              }
            ],
            "requiresSearch": true,
            "projectFiles": [
              {
                "path": "analytics/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "analytics/api.py",
                "brokenSource": "from analytics.statistics import mean\n\ndef solve(values):\n    return mean(values)\n",
                "referenceSource": "from analytics.statistics import mean\n\ndef solve(values):\n    return mean(values)\n",
                "mutable": false
              },
              {
                "path": "analytics/statistics.py",
                "brokenSource": "def mean(values):\n    return float(sum(values))\n",
                "referenceSource": "def mean(values):\n    return sum(values) / len(values) if values else 0.0\n",
                "mutable": true
              },
              {
                "path": "archive/statistics.py",
                "brokenSource": "# Archivierte Variante; nicht Bestandteil der öffentlichen API.\ndef mean(values):\n    return 123.0\n",
                "referenceSource": "# Archivierte Variante; nicht Bestandteil der öffentlichen API.\ndef mean(values):\n    return 123.0\n",
                "mutable": false
              },
              {
                "path": "docs/statistics.md",
                "brokenSource": "Die Analytics-API delegiert Berechnungen an das aktive Paket. Archivdateien sind unverändert zu erhalten.\n",
                "referenceSource": "Die Analytics-API delegiert Berechnungen an das aktive Paket. Archivdateien sind unverändert zu erhalten.\n",
                "mutable": false
              }
            ],
            "moduleProbes": [
              {
                "module": "analytics.statistics",
                "member": "mean",
                "cases": [
                  {
                    "arguments": [
                      [
                        2,
                        4
                      ]
                    ],
                    "expected": 3,
                    "expectedType": "float"
                  },
                  {
                    "arguments": [
                      []
                    ],
                    "expected": 0,
                    "expectedType": "float"
                  },
                  {
                    "arguments": [
                      [
                        -8,
                        2,
                        3
                      ]
                    ],
                    "expected": -1,
                    "expectedType": "float"
                  },
                  {
                    "arguments": [
                      [
                        7
                      ]
                    ],
                    "expected": 7,
                    "expectedType": "float"
                  },
                  {
                    "arguments": [
                      [
                        0,
                        1
                      ]
                    ],
                    "expected": 0.5,
                    "expectedType": "float"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "03-clamp",
            "requirement": "solve(value, lower, upper) begrenzt value auf das inklusive Intervall; lower <= upper. Repariere den Fehler im Intervallkern und die zusätzliche Verschiebung in der API-Schicht. Beide Module müssen korrigiert werden; die öffentliche Fassade und Tests bleiben unverändert.",
            "brokenSource": "from limits.api import solve\n",
            "referenceSource": "from limits.api import solve\n",
            "cases": [
              {
                "arguments": [
                  12,
                  0,
                  10
                ],
                "expected": 10,
                "expectedType": "int"
              },
              {
                "arguments": [
                  -4,
                  0,
                  10
                ],
                "expected": 0,
                "expectedType": "int"
              },
              {
                "arguments": [
                  3,
                  0,
                  10
                ],
                "expected": 3,
                "expectedType": "int"
              },
              {
                "arguments": [
                  8,
                  8,
                  8
                ],
                "expected": 8,
                "expectedType": "int"
              },
              {
                "arguments": [
                  -7,
                  -9,
                  -2
                ],
                "expected": -7,
                "expectedType": "int"
              }
            ],
            "projectFiles": [
              {
                "path": "limits/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "limits/core.py",
                "brokenSource": "def clip(value, lower, upper):\n    return min(lower, max(upper, value))\n",
                "referenceSource": "def clip(value, lower, upper):\n    return max(lower, min(upper, value))\n",
                "mutable": true
              },
              {
                "path": "limits/api.py",
                "brokenSource": "from limits.core import clip\n\ndef solve(value, lower, upper):\n    return clip(value, lower, upper) + 1\n",
                "referenceSource": "from limits.core import clip\n\ndef solve(value, lower, upper):\n    return clip(value, lower, upper)\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "limits.core",
                "member": "clip",
                "cases": [
                  {
                    "arguments": [
                      12,
                      0,
                      10
                    ],
                    "expected": 10,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      -4,
                      0,
                      10
                    ],
                    "expected": 0,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      3,
                      0,
                      10
                    ],
                    "expected": 3,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      8,
                      8,
                      8
                    ],
                    "expected": 8,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      -7,
                      -9,
                      -2
                    ],
                    "expected": -7,
                    "expectedType": "int"
                  }
                ],
                "callable": true
              },
              {
                "module": "limits.api",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      12,
                      0,
                      10
                    ],
                    "expected": 10,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      -4,
                      0,
                      10
                    ],
                    "expected": 0,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      3,
                      0,
                      10
                    ],
                    "expected": 3,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      8,
                      8,
                      8
                    ],
                    "expected": 8,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      -7,
                      -9,
                      -2
                    ],
                    "expected": -7,
                    "expectedType": "int"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "04-slug",
            "requirement": "solve(text) wandelt Text in Kleinbuchstaben um, ersetzt jede Folge von Nicht-ASCII-Buchstaben/Ziffern durch einen Bindestrich und entfernt Bindestriche am Rand. Korrigiere das vorhandene Textmodul; die öffentliche API bleibt erhalten.",
            "brokenSource": "from texttools.names import solve\n",
            "referenceSource": "from texttools.names import solve\n",
            "cases": [
              {
                "arguments": [
                  "  Hello, WORLD! "
                ],
                "expected": "hello-world",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "A___B"
                ],
                "expected": "a-b",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "###"
                ],
                "expected": "",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "v2.7 / GO"
                ],
                "expected": "v2-7-go",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "Ä Ö X"
                ],
                "expected": "x",
                "expectedType": "str"
              }
            ],
            "projectFiles": [
              {
                "path": "texttools/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "texttools/names.py",
                "brokenSource": "def solve(text):\n    return text.lower().replace(' ', '-')\n",
                "referenceSource": "import re\n\ndef solve(text):\n    return re.sub(r'[^a-z0-9]+', '-', text.lower()).strip('-')\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "texttools.names",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      "  Hello, WORLD! "
                    ],
                    "expected": "hello-world",
                    "expectedType": "str"
                  },
                  {
                    "arguments": [
                      "A___B"
                    ],
                    "expected": "a-b",
                    "expectedType": "str"
                  },
                  {
                    "arguments": [
                      "###"
                    ],
                    "expected": "",
                    "expectedType": "str"
                  },
                  {
                    "arguments": [
                      "v2.7 / GO"
                    ],
                    "expected": "v2-7-go",
                    "expectedType": "str"
                  },
                  {
                    "arguments": [
                      "Ä Ö X"
                    ],
                    "expected": "x",
                    "expectedType": "str"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "05-boolean",
            "requirement": "solve(text) trimmt Leerraum und ignoriert Großschreibung. true/yes/1 ergeben True, false/no/0 ergeben False, sonst None. Repariere den Parser und die API-Schicht, die derzeit False verwirft; beide bestehenden Module ändern, öffentliche Fassade erhalten.",
            "brokenSource": "from config.settings import solve\n",
            "referenceSource": "from config.settings import solve\n",
            "cases": [
              {
                "arguments": [
                  " FALSE "
                ],
                "expected": false,
                "expectedType": "bool"
              },
              {
                "arguments": [
                  " yes "
                ],
                "expected": true,
                "expectedType": "bool"
              },
              {
                "arguments": [
                  "1"
                ],
                "expected": true,
                "expectedType": "bool"
              },
              {
                "arguments": [
                  "NO"
                ],
                "expected": false,
                "expectedType": "bool"
              },
              {
                "arguments": [
                  "unknown"
                ],
                "expected": null,
                "expectedType": "NoneType"
              },
              {
                "arguments": [
                  ""
                ],
                "expected": null,
                "expectedType": "NoneType"
              }
            ],
            "projectFiles": [
              {
                "path": "config/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "config/values.py",
                "brokenSource": "def parse_flag(text):\n    return bool(text)\n",
                "referenceSource": "def parse_flag(text):\n    value = text.strip().lower()\n    return True if value in ('true', 'yes', '1') else False if value in ('false', 'no', '0') else None\n",
                "mutable": true
              },
              {
                "path": "config/settings.py",
                "brokenSource": "from config.values import parse_flag\n\ndef solve(text):\n    return parse_flag(text) or None\n",
                "referenceSource": "from config.values import parse_flag\n\ndef solve(text):\n    return parse_flag(text)\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "config.values",
                "member": "parse_flag",
                "cases": [
                  {
                    "arguments": [
                      " FALSE "
                    ],
                    "expected": false,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      " yes "
                    ],
                    "expected": true,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "1"
                    ],
                    "expected": true,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "NO"
                    ],
                    "expected": false,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "unknown"
                    ],
                    "expected": null,
                    "expectedType": "NoneType"
                  },
                  {
                    "arguments": [
                      ""
                    ],
                    "expected": null,
                    "expectedType": "NoneType"
                  }
                ],
                "callable": true
              },
              {
                "module": "config.settings",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      " FALSE "
                    ],
                    "expected": false,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      " yes "
                    ],
                    "expected": true,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "1"
                    ],
                    "expected": true,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "NO"
                    ],
                    "expected": false,
                    "expectedType": "bool"
                  },
                  {
                    "arguments": [
                      "unknown"
                    ],
                    "expected": null,
                    "expectedType": "NoneType"
                  },
                  {
                    "arguments": [
                      ""
                    ],
                    "expected": null,
                    "expectedType": "NoneType"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "06-stable-unique",
            "requirement": "solve(values) entfernt doppelte Ganzzahlen und erhält die Reihenfolge des ersten Auftretens. Eingabeliste unverändert lassen. Ermittle mit coding.search die aktive Implementierung, bevor du korrigierst; Test-, Archiv- und Dokumentdateien erhalten.",
            "brokenSource": "from inventory.api import solve\n",
            "referenceSource": "from inventory.api import solve\n",
            "cases": [
              {
                "arguments": [
                  [
                    3,
                    1,
                    3,
                    2
                  ]
                ],
                "expected": [
                  3,
                  1,
                  2
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  []
                ],
                "expected": [],
                "expectedType": "list"
              },
              {
                "arguments": [
                  [
                    -1,
                    0,
                    -1,
                    2,
                    0
                  ]
                ],
                "expected": [
                  -1,
                  0,
                  2
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  [
                    5,
                    5,
                    5
                  ]
                ],
                "expected": [
                  5
                ],
                "expectedType": "list"
              }
            ],
            "requiresSearch": true,
            "projectFiles": [
              {
                "path": "inventory/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "inventory/api.py",
                "brokenSource": "from inventory.sequence import stable_unique\n\ndef solve(values):\n    return stable_unique(values)\n",
                "referenceSource": "from inventory.sequence import stable_unique\n\ndef solve(values):\n    return stable_unique(values)\n",
                "mutable": false
              },
              {
                "path": "inventory/sequence.py",
                "brokenSource": "def stable_unique(values):\n    return sorted(set(values))\n",
                "referenceSource": "def stable_unique(values):\n    return list(dict.fromkeys(values))\n",
                "mutable": true
              },
              {
                "path": "archive/sequence.py",
                "brokenSource": "# Alte Importversion; wird nicht verwendet.\ndef stable_unique(values):\n    return list(set(values))\n",
                "referenceSource": "# Alte Importversion; wird nicht verwendet.\ndef stable_unique(values):\n    return list(set(values))\n",
                "mutable": false
              },
              {
                "path": "docs/import-flow.md",
                "brokenSource": "Die öffentliche API nutzt das Paket inventory. Archivcode ist unverändert zu erhalten.\n",
                "referenceSource": "Die öffentliche API nutzt das Paket inventory. Archivcode ist unverändert zu erhalten.\n",
                "mutable": false
              }
            ],
            "moduleProbes": [
              {
                "module": "inventory.sequence",
                "member": "stable_unique",
                "cases": [
                  {
                    "arguments": [
                      [
                        3,
                        1,
                        3,
                        2
                      ]
                    ],
                    "expected": [
                      3,
                      1,
                      2
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      []
                    ],
                    "expected": [],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        -1,
                        0,
                        -1,
                        2,
                        0
                      ]
                    ],
                    "expected": [
                      -1,
                      0,
                      2
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        5,
                        5,
                        5
                      ]
                    ],
                    "expected": [
                      5
                    ],
                    "expectedType": "list"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "07-chunks",
            "requirement": "solve(values, size) zerlegt die Liste in aufeinanderfolgende Teillisten mit höchstens size Elementen, einschließlich Rest; size ist positiv. In der Seitenerzeugung und der Serviceschicht gehen Daten verloren. Beide Module korrigieren, Eingabeliste und öffentliche API erhalten.",
            "brokenSource": "from pagination.service import solve\n",
            "referenceSource": "from pagination.service import solve\n",
            "cases": [
              {
                "arguments": [
                  [
                    1,
                    2,
                    3,
                    4,
                    5
                  ],
                  2
                ],
                "expected": [
                  [
                    1,
                    2
                  ],
                  [
                    3,
                    4
                  ],
                  [
                    5
                  ]
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  [],
                  3
                ],
                "expected": [],
                "expectedType": "list"
              },
              {
                "arguments": [
                  [
                    9
                  ],
                  8
                ],
                "expected": [
                  [
                    9
                  ]
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  [
                    4,
                    7
                  ],
                  1
                ],
                "expected": [
                  [
                    4
                  ],
                  [
                    7
                  ]
                ],
                "expectedType": "list"
              }
            ],
            "projectFiles": [
              {
                "path": "pagination/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "pagination/pages.py",
                "brokenSource": "def chunks(values, size):\n    return [values[i:i+size] for i in range(0, len(values)-size+1, size)]\n",
                "referenceSource": "def chunks(values, size):\n    return [values[i:i+size] for i in range(0, len(values), size)]\n",
                "mutable": true
              },
              {
                "path": "pagination/service.py",
                "brokenSource": "from pagination.pages import chunks\n\ndef solve(values, size):\n    return chunks(values, size)[:-1]\n",
                "referenceSource": "from pagination.pages import chunks\n\ndef solve(values, size):\n    return chunks(values, size)\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "pagination.pages",
                "member": "chunks",
                "cases": [
                  {
                    "arguments": [
                      [
                        1,
                        2,
                        3,
                        4,
                        5
                      ],
                      2
                    ],
                    "expected": [
                      [
                        1,
                        2
                      ],
                      [
                        3,
                        4
                      ],
                      [
                        5
                      ]
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [],
                      3
                    ],
                    "expected": [],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        9
                      ],
                      8
                    ],
                    "expected": [
                      [
                        9
                      ]
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        4,
                        7
                      ],
                      1
                    ],
                    "expected": [
                      [
                        4
                      ],
                      [
                        7
                      ]
                    ],
                    "expectedType": "list"
                  }
                ],
                "callable": true
              },
              {
                "module": "pagination.service",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      [
                        1,
                        2,
                        3,
                        4,
                        5
                      ],
                      2
                    ],
                    "expected": [
                      [
                        1,
                        2
                      ],
                      [
                        3,
                        4
                      ],
                      [
                        5
                      ]
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [],
                      3
                    ],
                    "expected": [],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        9
                      ],
                      8
                    ],
                    "expected": [
                      [
                        9
                      ]
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      [
                        4,
                        7
                      ],
                      1
                    ],
                    "expected": [
                      [
                        4
                      ],
                      [
                        7
                      ]
                    ],
                    "expectedType": "list"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "08-date",
            "requirement": "solve(date_text, days) addiert days Kalendertage zum ISO-Datum YYYY-MM-DD und liefert ein ISO-Datum; Monats-, Jahres- und Schaltjahresgrenzen berücksichtigen.",
            "brokenSource": "def solve(date_text, days):\n    return date_text\n",
            "referenceSource": "from datetime import date, timedelta\n\ndef solve(date_text, days):\n    return (date.fromisoformat(date_text) + timedelta(days=days)).isoformat()\n",
            "cases": [
              {
                "arguments": [
                  "2024-02-28",
                  1
                ],
                "expected": "2024-02-29",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "2025-12-31",
                  1
                ],
                "expected": "2026-01-01",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "2024-03-01",
                  -1
                ],
                "expected": "2024-02-29",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "2000-01-01",
                  0
                ],
                "expected": "2000-01-01",
                "expectedType": "str"
              },
              {
                "arguments": [
                  "2025-01-31",
                  2
                ],
                "expected": "2025-02-02",
                "expectedType": "str"
              }
            ]
          },
          {
            "id": "09-csv",
            "requirement": "solve(line) liest genau eine CSV-Zeile gemäß Python csv.reader Standarddialekt. Gequotete Kommas, doppelte Anführungszeichen, leere Felder und absichtlicher Leerraum in Feldern müssen erhalten bleiben. Repariere Parser und API-Nachverarbeitung in ihren bestehenden Modulen.",
            "brokenSource": "from csvio.api import solve\n",
            "referenceSource": "from csvio.api import solve\n",
            "cases": [
              {
                "arguments": [
                  "a,\"b,c\",d"
                ],
                "expected": [
                  "a",
                  "b,c",
                  "d"
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  ",x,"
                ],
                "expected": [
                  "",
                  "x",
                  ""
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  "\"a\"\"b\",c"
                ],
                "expected": [
                  "a\"b",
                  "c"
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  "plain"
                ],
                "expected": [
                  "plain"
                ],
                "expectedType": "list"
              },
              {
                "arguments": [
                  "  x  ,\" a \""
                ],
                "expected": [
                  "  x  ",
                  " a "
                ],
                "expectedType": "list"
              }
            ],
            "projectFiles": [
              {
                "path": "csvio/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "csvio/parser.py",
                "brokenSource": "def parse_row(line):\n    return line.split(',')\n",
                "referenceSource": "import csv\n\ndef parse_row(line):\n    return next(csv.reader([line]))\n",
                "mutable": true
              },
              {
                "path": "csvio/api.py",
                "brokenSource": "from csvio.parser import parse_row\n\ndef solve(line):\n    return [value.strip() for value in parse_row(line)]\n",
                "referenceSource": "from csvio.parser import parse_row\n\ndef solve(line):\n    return parse_row(line)\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "csvio.parser",
                "member": "parse_row",
                "cases": [
                  {
                    "arguments": [
                      "a,\"b,c\",d"
                    ],
                    "expected": [
                      "a",
                      "b,c",
                      "d"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      ",x,"
                    ],
                    "expected": [
                      "",
                      "x",
                      ""
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "\"a\"\"b\",c"
                    ],
                    "expected": [
                      "a\"b",
                      "c"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "plain"
                    ],
                    "expected": [
                      "plain"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "  x  ,\" a \""
                    ],
                    "expected": [
                      "  x  ",
                      " a "
                    ],
                    "expectedType": "list"
                  }
                ],
                "callable": true
              },
              {
                "module": "csvio.api",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      "a,\"b,c\",d"
                    ],
                    "expected": [
                      "a",
                      "b,c",
                      "d"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      ",x,"
                    ],
                    "expected": [
                      "",
                      "x",
                      ""
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "\"a\"\"b\",c"
                    ],
                    "expected": [
                      "a\"b",
                      "c"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "plain"
                    ],
                    "expected": [
                      "plain"
                    ],
                    "expectedType": "list"
                  },
                  {
                    "arguments": [
                      "  x  ,\" a \""
                    ],
                    "expected": [
                      "  x  ",
                      " a "
                    ],
                    "expectedType": "list"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "10-backoff",
            "requirement": "solve(attempt, base, cap) liefert min(cap, base * 2**attempt); attempt >= 0, base/cap positiv. Die Retry-Konfiguration und Berechnung sind fehlerhaft. Starte zuerst das geschützte Diagnoseprogramm mit exakt ['-B','diagnose.py']. Seine Ausgabe ist absichtlich lang: Lies den mittleren Beleg GO_BENCH_MIDDLE_RETRY_DIAGNOSTIC aus der tatsächlich gespeicherten Prozessausgabe mit coding.readOutput nach, bei Bedarf zuvor coding.searchRunEvidence. Starte den Prozess nicht bloß zum erneuten Lesen derselben Ausgabe. Korrigiere beide bestehenden Netzwerkmodule und prüfe anschließend die Tests.",
            "brokenSource": "from network.retries import solve\n",
            "referenceSource": "from network.retries import solve\n",
            "cases": [
              {
                "arguments": [
                  0,
                  2,
                  20
                ],
                "expected": 2,
                "expectedType": "int"
              },
              {
                "arguments": [
                  4,
                  2,
                  20
                ],
                "expected": 20,
                "expectedType": "int"
              },
              {
                "arguments": [
                  2,
                  3,
                  100
                ],
                "expected": 12,
                "expectedType": "int"
              },
              {
                "arguments": [
                  9,
                  1,
                  7
                ],
                "expected": 7,
                "expectedType": "int"
              },
              {
                "arguments": [
                  1,
                  3,
                  6
                ],
                "expected": 6,
                "expectedType": "int"
              }
            ],
            "requiresOutputRead": true,
            "diagnosticMarker": "GO_BENCH_MIDDLE_RETRY_DIAGNOSTIC",
            "projectFiles": [
              {
                "path": "network/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "network/config.py",
                "brokenSource": "GROWTH_FACTOR = 3\n",
                "referenceSource": "GROWTH_FACTOR = 2\n",
                "mutable": true
              },
              {
                "path": "network/retries.py",
                "brokenSource": "from network.config import GROWTH_FACTOR\n\ndef solve(attempt, base, cap):\n    return min(cap, base * GROWTH_FACTOR * attempt)\n",
                "referenceSource": "from network.config import GROWTH_FACTOR\n\ndef solve(attempt, base, cap):\n    return min(cap, base * (GROWTH_FACTOR ** attempt))\n",
                "mutable": true
              },
              {
                "path": "diagnose.py",
                "brokenSource": "from network import config\nfrom network.retries import solve\nimport sys\n\nfor index in range(1600):\n    print(f'prelude {index:04d}: connection timing sample retained for diagnostic analysis')\nprint('GO_BENCH_MIDDLE_RETRY_DIAGNOSTIC: network/config.py defines GROWTH_FACTOR; network/retries.py applies it. Expected exponential growth factor=2 and cap preservation. observed_factor=' + str(config.GROWTH_FACTOR))\nfor index in range(1600):\n    print(f'epilogue {index:04d}: connection timing sample retained for diagnostic analysis')\nprint('diagnostic complete; middle contains the configuration findings', file=sys.stderr)\nsys.exit(0 if config.GROWTH_FACTOR == 2 and solve(0, 2, 20) == 2 and solve(4, 2, 20) == 20 else 1)\n",
                "referenceSource": "from network import config\nfrom network.retries import solve\nimport sys\n\nfor index in range(1600):\n    print(f'prelude {index:04d}: connection timing sample retained for diagnostic analysis')\nprint('GO_BENCH_MIDDLE_RETRY_DIAGNOSTIC: network/config.py defines GROWTH_FACTOR; network/retries.py applies it. Expected exponential growth factor=2 and cap preservation. observed_factor=' + str(config.GROWTH_FACTOR))\nfor index in range(1600):\n    print(f'epilogue {index:04d}: connection timing sample retained for diagnostic analysis')\nprint('diagnostic complete; middle contains the configuration findings', file=sys.stderr)\nsys.exit(0 if config.GROWTH_FACTOR == 2 and solve(0, 2, 20) == 2 and solve(4, 2, 20) == 20 else 1)\n",
                "mutable": false
              }
            ],
            "moduleProbes": [
              {
                "module": "network.retries",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      0,
                      2,
                      20
                    ],
                    "expected": 2,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      4,
                      2,
                      20
                    ],
                    "expected": 20,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      2,
                      3,
                      100
                    ],
                    "expected": 12,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      9,
                      1,
                      7
                    ],
                    "expected": 7,
                    "expectedType": "int"
                  },
                  {
                    "arguments": [
                      1,
                      3,
                      6
                    ],
                    "expected": 6,
                    "expectedType": "int"
                  }
                ],
                "callable": true
              },
              {
                "module": "network.config",
                "member": "GROWTH_FACTOR",
                "cases": [
                  {
                    "arguments": [],
                    "expected": 2,
                    "expectedType": "int"
                  }
                ],
                "callable": false
              }
            ]
          },
          {
            "id": "11-migration",
            "requirement": "solve(record) liefert eine neue Kopie des Dictionaries. Nur legacy_name entfernen; wenn name fehlt, dessen vorhandenen Legacy-Wert übernehmen. Alle weiteren Felder und ein vorhandenes name exakt erhalten, Eingabe nicht verändern. Repariere die Migration und die Repository-Schicht, die derzeit unbekannte Felder verwirft. Beide bestehenden Module korrigieren.",
            "brokenSource": "from records.repository import solve\n",
            "referenceSource": "from records.repository import solve\n",
            "cases": [
              {
                "arguments": [
                  {
                    "legacy_name": "Ada",
                    "age": 37
                  }
                ],
                "expected": {
                  "name": "Ada",
                  "age": 37
                },
                "expectedType": "dict"
              },
              {
                "arguments": [
                  {
                    "name": "New",
                    "legacy_name": "Old",
                    "enabled": true
                  }
                ],
                "expected": {
                  "name": "New",
                  "enabled": true
                },
                "expectedType": "dict"
              },
              {
                "arguments": [
                  {
                    "extra": [
                      1,
                      2
                    ]
                  }
                ],
                "expected": {
                  "extra": [
                    1,
                    2
                  ]
                },
                "expectedType": "dict"
              },
              {
                "arguments": [
                  {}
                ],
                "expected": {},
                "expectedType": "dict"
              }
            ],
            "projectFiles": [
              {
                "path": "records/__init__.py",
                "brokenSource": "",
                "referenceSource": "",
                "mutable": false
              },
              {
                "path": "records/migration.py",
                "brokenSource": "def migrate(record):\n    record['name'] = record.pop('legacy_name', '')\n    return record\n",
                "referenceSource": "def migrate(record):\n    result = dict(record)\n    if 'name' not in result and 'legacy_name' in result:\n        result['name'] = result['legacy_name']\n    result.pop('legacy_name', None)\n    return result\n",
                "mutable": true
              },
              {
                "path": "records/repository.py",
                "brokenSource": "from records.migration import migrate\n\ndef solve(record):\n    return {key: value for key, value in migrate(record).items() if key == 'name'}\n",
                "referenceSource": "from records.migration import migrate\n\ndef solve(record):\n    return migrate(record)\n",
                "mutable": true
              }
            ],
            "moduleProbes": [
              {
                "module": "records.migration",
                "member": "migrate",
                "cases": [
                  {
                    "arguments": [
                      {
                        "legacy_name": "Ada",
                        "age": 37
                      }
                    ],
                    "expected": {
                      "name": "Ada",
                      "age": 37
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {
                        "name": "New",
                        "legacy_name": "Old",
                        "enabled": true
                      }
                    ],
                    "expected": {
                      "name": "New",
                      "enabled": true
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {
                        "extra": [
                          1,
                          2
                        ]
                      }
                    ],
                    "expected": {
                      "extra": [
                        1,
                        2
                      ]
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {}
                    ],
                    "expected": {},
                    "expectedType": "dict"
                  }
                ],
                "callable": true
              },
              {
                "module": "records.repository",
                "member": "solve",
                "cases": [
                  {
                    "arguments": [
                      {
                        "legacy_name": "Ada",
                        "age": 37
                      }
                    ],
                    "expected": {
                      "name": "Ada",
                      "age": 37
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {
                        "name": "New",
                        "legacy_name": "Old",
                        "enabled": true
                      }
                    ],
                    "expected": {
                      "name": "New",
                      "enabled": true
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {
                        "extra": [
                          1,
                          2
                        ]
                      }
                    ],
                    "expected": {
                      "extra": [
                        1,
                        2
                      ]
                    },
                    "expectedType": "dict"
                  },
                  {
                    "arguments": [
                      {}
                    ],
                    "expected": {},
                    "expectedType": "dict"
                  }
                ],
                "callable": true
              }
            ]
          },
          {
            "id": "12-research-query",
            "requirement": "solve(pairs) URL-kodiert eine geordnete Liste aus Schlüssel-/Wert-Paaren. Listenwerte als wiederholte Schlüssel, Leerraum mit +, leere Listen weglassen. Prüfe urllib.parse.urlencode und doseq in der angegebenen Python-3.13-Originaldokumentation und belege die Entscheidung mit der tatsächlich gelesenen Quelle.",
            "brokenSource": "from urllib.parse import urlencode\n\ndef solve(pairs):\n    return urlencode([tuple(pair) for pair in pairs])\n",
            "referenceSource": "from urllib.parse import urlencode\n\ndef solve(pairs):\n    return urlencode([tuple(pair) for pair in pairs], doseq=True)\n",
            "cases": [
              {
                "arguments": [
                  [
                    [
                      "q",
                      [
                        "a b",
                        "c/d"
                      ]
                    ]
                  ]
                ],
                "expected": "q=a+b&q=c%2Fd",
                "expectedType": "str"
              },
              {
                "arguments": [
                  [
                    [
                      "empty",
                      []
                    ],
                    [
                      "x",
                      "1"
                    ]
                  ]
                ],
                "expected": "x=1",
                "expectedType": "str"
              },
              {
                "arguments": [
                  [
                    [
                      "unicode",
                      "Grüße"
                    ],
                    [
                      "n",
                      [
                        2,
                        3
                      ]
                    ]
                  ]
                ],
                "expected": "unicode=Gr%C3%BC%C3%9Fe&n=2&n=3",
                "expectedType": "str"
              }
            ],
            "research": true
          }
        ]
        """;
}
