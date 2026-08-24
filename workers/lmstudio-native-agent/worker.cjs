"use strict";

const readline = require("node:readline");
const {
  Chat,
  LMStudioClient,
  unimplementedRawFunctionTool,
} = require("@lmstudio/sdk");

const TOKEN_PATTERN = /^sk-lm-(?<clientIdentifier>[a-zA-Z0-9]{8}):(?<clientPasskey>[a-zA-Z0-9]{20})$/;

let client = null;
let clientIdentity = null;
let activeRequest = null;

const PARALLEL_SESSION_CONFIG_KEY = "llm.load.numParallelSessions";
const TOOL_ARGUMENT_IDLE_TIMEOUT_MS = 10 * 60_000;
const sdkCompatibilityMarker = Symbol("goSinglePredictionLoadConfig");

function emit(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function normalizeBaseUrl(value) {
  const url = new URL(value);
  if (url.protocol === "http:") {
    url.protocol = "ws:";
  } else if (url.protocol === "https:") {
    url.protocol = "wss:";
  }
  url.pathname = url.pathname.replace(/\/$/, "");
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/, "");
}

async function disposeClient() {
  if (client === null) {
    return;
  }

  const current = client;
  client = null;
  clientIdentity = null;
  try {
    await current[Symbol.asyncDispose]();
  } catch {
    // The owning GO process will recreate the channel on the next request.
  }
}

async function getClient(baseUrl, apiToken) {
  const match = TOKEN_PATTERN.exec(apiToken || "");
  if (!match?.groups) {
    throw new Error("LM Studio API token has an unsupported format.");
  }

  const normalizedBaseUrl = normalizeBaseUrl(baseUrl);
  const identity = `${normalizedBaseUrl}\u0000${match.groups.clientIdentifier}`;
  if (client !== null && clientIdentity === identity) {
    return client;
  }

  await disposeClient();
  client = new LMStudioClient({
    baseUrl: normalizedBaseUrl,
    clientIdentifier: match.groups.clientIdentifier,
    clientPasskey: match.groups.clientPasskey,
    verboseErrorMessages: true,
  });
  installSinglePredictionLoadConfig(client);
  clientIdentity = identity;
  return client;
}

function installSinglePredictionLoadConfig(sdkClient) {
  const namespace = sdkClient?.llm;
  if (!namespace || namespace[sdkCompatibilityMarker]) {
    return;
  }

  // LM Studio 0.4.21 already supports numParallelSessions, while the currently
  // published @lmstudio/sdk 1.5.0 does not yet expose maxParallelPredictions in
  // LLMLoadModelConfig. Add the same KV field used by the current upstream SDK
  // at its load-config boundary. This keeps loading and inference on the native
  // SDK channel and can be removed once the public package contains the field.
  const convertLoadConfig = namespace.loadConfigToKVConfig;
  if (typeof convertLoadConfig !== "function") {
    throw new Error("The LM Studio SDK does not expose its load configuration boundary.");
  }

  namespace.loadConfigToKVConfig = function loadConfigToSinglePredictionKVConfig(config) {
    const converted = convertLoadConfig.call(this, config);
    if (!converted || !Array.isArray(converted.fields)) {
      throw new Error("The LM Studio SDK returned an invalid load configuration.");
    }

    return {
      ...converted,
      fields: [
        ...converted.fields.filter((field) => field?.key !== PARALLEL_SESSION_CONFIG_KEY),
        { key: PARALLEL_SESSION_CONFIG_KEY, value: 1 },
      ],
    };
  };
  Object.defineProperty(namespace, sdkCompatibilityMarker, { value: true });
}

function textPart(text) {
  return { type: "text", text: text || "" };
}

function toHistory(messages) {
  return {
    messages: messages.map((message) => {
      const role = String(message.role || "user").toLowerCase();
      if (role === "assistant") {
        const content = [];
        if (message.content) {
          content.push(textPart(message.content));
        }
        for (const call of message.toolCalls || []) {
          content.push({
            type: "toolCallRequest",
            toolCallRequest: {
              id: call.id || undefined,
              type: "function",
              name: call.name,
              arguments: call.arguments || {},
            },
          });
        }
        return { role: "assistant", content };
      }

      if (role === "tool") {
        return {
          role: "tool",
          content: [{
            type: "toolCallResult",
            toolCallId: message.toolCallId || undefined,
            content: message.content || "",
          }],
        };
      }

      return {
        role: role === "system" ? "system" : "user",
        content: [textPart(message.content)],
      };
    }),
  };
}

function selectToolDefinitions(tools, requiredToolName) {
  return requiredToolName
    ? tools.filter((tool) => tool.name === requiredToolName)
    : tools;
}

function toAgentTools(tools, requiredToolName) {
  return selectToolDefinitions(tools, requiredToolName).map((tool) => unimplementedRawFunctionTool({
      name: tool.name,
      description: tool.description,
      parametersJsonSchema: tool.parameters,
  }));
}

function applySampling(options, sampling) {
  if (!sampling) {
    return;
  }
  if (Number.isFinite(sampling.temperature)) options.temperature = sampling.temperature;
  if (Number.isFinite(sampling.topP)) options.topPSampling = sampling.topP;
  if (Number.isFinite(sampling.topK)) options.topKSampling = sampling.topK;
  if (Number.isFinite(sampling.minP)) options.minPSampling = sampling.minP;
  if (Number.isFinite(sampling.repeatPenalty)) options.repeatPenalty = sampling.repeatPenalty;
}

function sameModel(value, expected) {
  return String(value || "").localeCompare(String(expected || ""), undefined, { sensitivity: "accent" }) === 0;
}

function isTransientSdkTransportFailure(error) {
  const messages = [];
  let current = error;
  for (let depth = 0; current && depth < 6; depth++) {
    messages.push(String(current.message || current));
    current = current.cause;
  }
  return /\bterminated\b|fetch failed|channel error|ECONNRESET|ECONNABORTED|socket hang up/i
    .test(messages.join("\n"));
}

async function waitForSdkRecovery(milliseconds, signal) {
  await new Promise((resolve, reject) => {
    const finish = () => {
      signal.removeEventListener("abort", abort);
      resolve();
    };
    const timer = setTimeout(finish, milliseconds);
    const abort = () => {
      clearTimeout(timer);
      signal.removeEventListener("abort", abort);
      reject(signal.reason || new Error("The native SDK recovery was cancelled."));
    };
    if (signal.aborted) {
      abort();
      return;
    }
    signal.addEventListener("abort", abort, { once: true });
  });
}

function readLoadConfigValue(loadConfig, propertyName, kvName) {
  if (loadConfig && Object.prototype.hasOwnProperty.call(loadConfig, propertyName)) {
    return loadConfig[propertyName];
  }

  const fields = Array.isArray(loadConfig?.fields) ? loadConfig.fields : [];
  const qualifiedName = `llm.load.${kvName}`;
  return fields.find((field) => field?.key === qualifiedName || field?.key === kvName)?.value;
}

function readParallelPredictions(loadConfig) {
  const value = readLoadConfigValue(loadConfig, "maxParallelPredictions", "numParallelSessions");
  return Number.isInteger(Number(value)) && Number(value) > 0 ? Number(value) : null;
}

async function listLoadedModels(sdkClient) {
  const [llms, embeddings] = await Promise.all([
    sdkClient.llm.listLoaded(),
    sdkClient.embedding.listLoaded(),
  ]);
  const entries = [];
  for (const model of [...llms, ...embeddings]) {
    const [info, loadConfig] = await Promise.all([
      model.getModelInfo(),
      model.getLoadConfig(),
    ]);
    entries.push({ model, info, loadConfig });
  }
  return entries;
}

async function modelCatalog(request, controller) {
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  const [downloaded, loaded] = await Promise.all([
    sdkClient.system.listDownloadedModels(),
    listLoadedModels(sdkClient),
  ]);
  controller.signal.throwIfAborted();
  const models = downloaded.map((model) => {
    const instances = loaded
      .filter((entry) => sameModel(entry.model.modelKey, model.modelKey))
      .map((entry) => ({
        id: entry.model.identifier,
        model_instance_id: entry.info.instanceReference || entry.model.identifier,
        config: {
          context_length: Number(entry.info.contextLength || model.maxContextLength || 0),
          parallel: readParallelPredictions(entry.loadConfig),
          flash_attention: readLoadConfigValue(entry.loadConfig, "flashAttention", "llama.flashAttention") !== false,
          offload_kv_cache_to_gpu: readLoadConfigValue(
            entry.loadConfig,
            "offloadKVCacheToGpu",
            "offloadKVCacheToGpu") !== false,
        },
      }));
    return {
      type: model.type,
      key: model.modelKey,
      display_name: model.displayName,
      max_context_length: Number(model.maxContextLength || 0),
      capabilities: {
        vision: Boolean(model.vision),
        trained_for_tool_use: Boolean(model.trainedForToolUse),
      },
      loaded_instances: instances,
    };
  });
  emit({ id: request.id, type: "result", models });
}

async function loadModel(request, controller) {
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  const namespace = request.isEmbedding ? sdkClient.embedding : sdkClient.llm;
  const downloaded = await sdkClient.system.listDownloadedModels(request.isEmbedding ? "embedding" : "llm");
  const selected = downloaded.find((model) => sameModel(model.modelKey, request.modelId));
  if (!selected) {
    throw new Error(`Configured LM Studio model is not downloaded: ${request.modelId}`);
  }

  const requestedContext = Math.max(2048, Number(request.contextLength) || 2048);
  const loaded = await namespace.listLoaded();
  for (const candidate of loaded.filter((model) => sameModel(model.modelKey, request.modelId))) {
    const [info, loadConfig] = await Promise.all([
      candidate.getModelInfo(),
      candidate.getLoadConfig(),
    ]);
    const configuredParallelPredictions = readParallelPredictions(loadConfig);
    if (Number(info.contextLength || 0) >= requestedContext
        && configuredParallelPredictions === 1) {
      emit({
        id: request.id,
        type: "result",
        instanceId: candidate.identifier,
        wasAlreadyLoaded: true,
      });
      return;
    }
    await candidate.unload();
  }

  const config = request.isEmbedding
    ? {
        contextLength: requestedContext,
        gpu: { ratio: "max", splitStrategy: "evenly" },
        keepModelInMemory: true,
      }
    : {
        contextLength: requestedContext,
        gpu: { ratio: "max", splitStrategy: "evenly" },
        gpuStrictVramCap: false,
        flashAttention: true,
        offloadKVCacheToGpu: true,
        keepModelInMemory: true,
      };
  const model = await namespace.load(selected.modelKey, {
    config,
    signal: controller.signal,
  });
  emit({
    id: request.id,
    type: "result",
    instanceId: model.identifier,
    wasAlreadyLoaded: false,
  });
}

async function unloadModels(request, controller) {
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  const requested = new Set((request.identifiers || []).map((value) => String(value).toLocaleLowerCase()));
  const preserve = new Set((request.preserveModelIds || []).map((value) => String(value).toLocaleLowerCase()));
  const loaded = await listLoadedModels(sdkClient);
  let unloaded = 0;
  for (const entry of loaded) {
    controller.signal.throwIfAborted();
    const keys = [entry.model.identifier, entry.model.modelKey, entry.info.instanceReference]
      .filter(Boolean)
      .map((value) => String(value).toLocaleLowerCase());
    const shouldUnload = request.unloadAll
      ? !keys.some((key) => preserve.has(key))
      : keys.some((key) => requested.has(key));
    if (shouldUnload) {
      await entry.model.unload();
      unloaded++;
    }
  }
  emit({ id: request.id, type: "result", unloaded });
}

async function createEmbeddings(request, controller) {
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  controller.signal.throwIfAborted();
  const model = sdkClient.embedding.model(request.modelId);
  const results = await model.embed(request.inputs || []);
  emit({ id: request.id, type: "result", embeddings: results.map((item) => item.embedding) });
}

async function analyzeImages(request, controller) {
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  const images = [];
  for (const imagePath of request.imagePaths || []) {
    controller.signal.throwIfAborted();
    images.push(await sdkClient.files.prepareImage(imagePath));
  }
  const chat = Chat.empty();
  chat.append("system", "Analysiere ausschliesslich die bereitgestellten Medien fachlich. Erfinde keine sichtbaren Details.");
  chat.append("user", request.prompt || "Analysiere die Medien.", { images });
  const model = await sdkClient.llm.model(request.modelId);
  const result = await model.respond(chat, {
    signal: controller.signal,
    contextOverflowPolicy: "stopAtLimit",
    temperature: 0.1,
  });
  emit({
    id: request.id,
    type: "result",
    content: result.nonReasoningContent || result.content || null,
  });
}

function errorPayload(id, code, error, rawContent, details = {}) {
  return {
    id,
    type: "error",
    code,
    message: error instanceof Error ? error.message : String(error),
    rawContent: rawContent || undefined,
    ...details,
  };
}

function nativeAgentError(code, message, rawContent, toolName) {
  const error = new Error(message);
  error.goCode = code;
  error.rawContent = rawContent;
  error.toolName = toolName;
  return error;
}

async function runNativeToolAct({
  request,
  controller,
  model,
  messages,
  agentTools,
  maximumOutputTokens,
}) {
  const toolCalls = [];
  const toolFailures = [];
  const argumentCharacters = new Map();
  const predictions = [];
  let lastPromptProgressBucket = -1;
  let acceptedToolCall = false;
  let lastGeneratedToolName = null;
  let argumentIdleTimer = null;
  let argumentGenerationTimedOut = false;
  const predictionController = new AbortController();
  const abortPrediction = () => predictionController.abort(controller.signal.reason);
  if (controller.signal.aborted) {
    abortPrediction();
  } else {
    controller.signal.addEventListener("abort", abortPrediction, { once: true });
  }
  const clearArgumentIdleTimer = () => {
    if (argumentIdleTimer !== null) {
      clearTimeout(argumentIdleTimer);
      argumentIdleTimer = null;
    }
  };
  const armArgumentIdleTimer = () => {
    clearArgumentIdleTimer();
    argumentIdleTimer = setTimeout(() => {
      argumentGenerationTimedOut = true;
      predictionController.abort(new Error("Native tool argument generation became idle."));
    }, TOOL_ARGUMENT_IDLE_TIMEOUT_MS);
  };
  const actOptions = {
    maxTokens: Math.max(1, Math.min(Number(maximumOutputTokens) || 8192, 65536)),
    contextOverflowPolicy: "stopAtLimit",
    signal: predictionController.signal,
    toolNaming: "passThrough",
    // Bionic and the public SDK use model.act() for tool generation. The
    // unimplemented tools deliberately stop the SDK loop after one validated
    // request so GO can execute it inside its own workspace trust boundary.
    // Up to two invalid generations may be corrected by the SDK itself.
    maxPredictionRounds: 3,
    allowParallelToolExecution: false,
    onPromptProcessingProgress: (_roundIndex, progress) => {
      const bounded = Math.max(0, Math.min(1, Number(progress) || 0));
      const bucket = Math.min(4, Math.floor(bounded * 4));
      if (bucket === lastPromptProgressBucket) {
        return;
      }
      lastPromptProgressBucket = bucket;
      emit({
        id: request.id,
        type: "promptProcessingProgress",
        progress: bounded,
      });
    },
    onFirstToken: () => emit({
      id: request.id,
      type: "generationStarted",
    }),
    onToolCallRequestStart: (_roundIndex, callId, info) => emit({
      id: request.id,
      type: "toolCallGenerationStart",
      callId,
      modelToolCallId: info.toolCallId,
    }),
    onToolCallRequestNameReceived: (_roundIndex, callId, name) => {
      lastGeneratedToolName = name;
      armArgumentIdleTimer();
      emit({
        id: request.id,
        type: "toolCallGenerationNameReceived",
        callId,
        name,
      });
    },
    onToolCallRequestArgumentFragmentGenerated: (_roundIndex, callId, content) => {
      armArgumentIdleTimer();
      const previous = argumentCharacters.get(callId) || 0;
      const current = previous + content.length;
      argumentCharacters.set(callId, current);
      if (previous === 0 || Math.floor(previous / 256) !== Math.floor(current / 256)) {
        emit({
          id: request.id,
          type: "toolCallGenerationArgumentFragmentGenerated",
          callId,
          characterCount: current,
        });
      }
    },
    onToolCallRequestEnd: (_roundIndex, callId, info) => {
      clearArgumentIdleTimer();
      const generated = info.toolCallRequest;
      emit({
        id: request.id,
        type: "toolCallGenerationEnd",
        callId,
        name: generated.name,
        characterCount: argumentCharacters.get(callId) || 0,
      });
    },
    onToolCallRequestFinalized: (_roundIndex, callId, info) => {
      const generated = info.toolCallRequest;
      toolCalls.push({
        id: generated.id || `call_${request.id}_${callId}`,
        name: generated.name,
        arguments: generated.arguments || {},
      });
    },
    onToolCallRequestFailure: (_roundIndex, callId, error) => {
      clearArgumentIdleTimer();
      toolFailures.push({ error, rawContent: error.rawContent });
      emit({
        id: request.id,
        type: "toolCallGenerationFailed",
        callId,
      });
    },
    guardToolCall: (_roundIndex, _callId, guard) => {
      if (acceptedToolCall) {
        guard.deny("GO accepts exactly one tool call per coding round.");
        return;
      }
      acceptedToolCall = true;
      guard.allow();
    },
    handleInvalidToolRequest: (error, generated) => {
      if (generated === undefined) {
        throw error;
      }
      return {
        error: "The generated tool arguments were invalid. Generate one corrected tool call.",
      };
    },
    onPredictionCompleted: (prediction) => {
      predictions.push(prediction);
    },
  };
  applySampling(actOptions, request.sampling);

  try {
    await model.act(toHistory(messages), agentTools, actOptions);
  } catch (error) {
    if (argumentGenerationTimedOut) {
      throw nativeAgentError(
        "tool_generation_timeout",
        "LM Studio did not continue native tool argument generation within ten minutes.",
        undefined,
        lastGeneratedToolName);
    }
    if (error && typeof error === "object" && lastGeneratedToolName) {
      error.toolName = lastGeneratedToolName;
    }
    throw error;
  } finally {
    clearArgumentIdleTimer();
    controller.signal.removeEventListener("abort", abortPrediction);
  }

  const result = predictions.at(-1);
  if (!result) {
    throw nativeAgentError(
      "prediction_failed",
      "LM Studio produced no prediction result.",
      undefined,
      lastGeneratedToolName);
  }
  if (toolFailures.length > 0 && toolCalls.length === 0) {
    const failure = toolFailures[0];
    throw nativeAgentError(
      "tool_generation_failed",
      failure.error instanceof Error ? failure.error.message : String(failure.error),
      failure.rawContent,
      lastGeneratedToolName);
  }
  if (result.stats.stopReason === "failed") {
    throw nativeAgentError(
      "prediction_failed",
      "LM Studio prediction failed.",
      undefined,
      lastGeneratedToolName);
  }

  return {
    content: result.nonReasoningContent || result.content || null,
    toolCalls,
    inputTokens: predictions.reduce(
      (total, prediction) => total + (prediction.stats.promptTokensCount || 0),
      0),
    outputTokens: predictions.reduce(
      (total, prediction) => total + (prediction.stats.predictedTokensCount || 0),
      0),
    hadReasoning: predictions.some((prediction) => Boolean(prediction.reasoningContent)),
    stopReason: result.stats.stopReason,
  };
}

async function predictOnce(request, controller) {
  emit({ id: request.id, type: "clientConnecting" });
  const sdkClient = await getClient(request.baseUrl, request.apiToken);
  emit({ id: request.id, type: "clientReady" });
  const model = await sdkClient.llm.model(request.modelId);
  emit({ id: request.id, type: "modelReady" });
  let lastPromptProgressBucket = -1;
  const selectedDefinitions = selectToolDefinitions(
    request.tools || [],
    request.requiredToolName);
  const baseOptions = {
    maxTokens: Math.max(1, Math.min(Number(request.maximumOutputTokens) || 8192, 65536)),
    contextOverflowPolicy: "stopAtLimit",
    signal: controller.signal,
    toolNaming: "passThrough",
  };
  applySampling(baseOptions, request.sampling);

  if (selectedDefinitions.length === 0) {
    const result = await model.respond(toHistory(request.messages || []), {
      ...baseOptions,
      onPromptProcessingProgress: (progress) => {
        const bounded = Math.max(0, Math.min(1, Number(progress) || 0));
        const bucket = Math.min(4, Math.floor(bounded * 4));
        if (bucket === lastPromptProgressBucket) {
          return;
        }
        lastPromptProgressBucket = bucket;
        emit({
          id: request.id,
          type: "promptProcessingProgress",
          progress: bounded,
        });
      },
      onFirstToken: () => emit({
        id: request.id,
        type: "generationStarted",
      }),
    });
    if (result.stats.stopReason === "failed") {
      emit(errorPayload(request.id, "prediction_failed", "LM Studio prediction failed."));
      return;
    }
    emit({
      id: request.id,
      type: "result",
      content: result.nonReasoningContent || result.content || null,
      toolCalls: [],
      inputTokens: result.stats.promptTokensCount || 0,
      outputTokens: result.stats.predictedTokensCount || 0,
      hadReasoning: Boolean(result.reasoningContent),
      stopReason: result.stats.stopReason,
    });
    return;
  }

  // Bionic uses one native model.act() prediction with the complete SDK tool
  // definitions. LM Studio emits the selected name first and then constrains
  // the argument object with that tool's schema inside the same prediction.
  // A separate routing prediction doubled backend churn and could terminate
  // the llama.cpp channel between name selection and argument generation.
  const result = await runNativeToolAct({
    request,
    controller,
    model,
    messages: request.messages || [],
    agentTools: toAgentTools(selectedDefinitions, request.requiredToolName),
    maximumOutputTokens: request.maximumOutputTokens,
  });

  emit({
    id: request.id,
    type: "result",
    content: result.content,
    toolCalls: result.toolCalls,
    inputTokens: result.inputTokens,
    outputTokens: result.outputTokens,
    hadReasoning: result.hadReasoning,
    stopReason: result.stopReason,
  });
}

async function predict(request, controller) {
  emit({ id: request.id, type: "requestAccepted" });
  let currentRequest = request;
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      await predictOnce(currentRequest, controller);
      return;
    } catch (error) {
      if (attempt >= 2
          || controller.signal.aborted
          || !isTransientSdkTransportFailure(error)) {
        throw error;
      }

      emit({
        id: request.id,
        type: "transportRetry",
        attempt: attempt + 1,
        toolName: error?.toolName || undefined,
      });
      if (error?.toolName
          && (request.tools || []).some((tool) => tool.name === error.toolName)) {
        // The first SDK prediction already selected a valid native tool. A
        // terminated channel must not route the complete catalog again: retry
        // only the selected schema so argument generation can resume with a
        // much smaller grammar and without changing the intended action.
        currentRequest = {
          ...request,
          requiredToolName: error.toolName,
        };
      }
      await disposeClient();
      await waitForSdkRecovery(1_000, controller.signal);
    }
  }
}

async function execute(request) {
  const controller = new AbortController();
  activeRequest = { id: request.id, controller };
  try {
    switch (request.type) {
      case "predict":
        await predict(request, controller);
        break;
      case "models":
        await modelCatalog(request, controller);
        break;
      case "loadModel":
        await loadModel(request, controller);
        break;
      case "unloadModels":
        await unloadModels(request, controller);
        break;
      case "embeddings":
        await createEmbeddings(request, controller);
        break;
      case "analyzeImages":
        await analyzeImages(request, controller);
        break;
      default:
        emit(errorPayload(request.id, "unknown_request", "Unknown native SDK request."));
        break;
    }
  } catch (error) {
    const code = controller.signal.aborted
      ? "cancelled"
      : error?.goCode || "native_sdk_error";
    emit(errorPayload(
      request.id,
      code,
      error,
      error?.rawContent,
      { toolName: error?.toolName || undefined }));
  } finally {
    if (activeRequest?.id === request.id) {
      activeRequest = null;
    }
  }
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", (line) => {
  let request;
  try {
    request = JSON.parse(line.replace(/^\uFEFF/, ""));
  } catch (error) {
    emit(errorPayload(null, "invalid_request_json", error));
    return;
  }

  if (request.type === "cancel") {
    if (activeRequest?.id === request.id) {
      activeRequest.controller.abort();
    }
    return;
  }
  if (request.type === "shutdown") {
    activeRequest?.controller.abort();
    void disposeClient().finally(() => process.exit(0));
    return;
  }
  if (activeRequest !== null) {
    emit(errorPayload(request.id, "busy", "The native agent already has an active prediction."));
    return;
  }
  void execute(request);
});

input.on("close", () => {
  activeRequest?.controller.abort();
  void disposeClient().finally(() => process.exit(0));
});

emit({ id: null, type: "ready" });
