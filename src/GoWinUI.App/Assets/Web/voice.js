(function () {
  "use strict";

  const targetSampleRate = 16000;
  const chunkStepSamples = 24000;
  const overlapSamples = 0;
  const preRollSamples = 4000;
  const minimumTurnSamples = 3200;
  // Zwei Sekunden Sprechpause bestätigen eine vollständige Äußerung.
  const silenceToFinishSamples = 32000;
  const capture = {
    active: false,
    starting: false,
    suspended: true,
    stream: null,
    context: null,
    source: null,
    analyser: null,
    processor: null,
    outputSink: null,
    visualizerFrame: 0,
    preRoll: [],
    turn: null,
    noiseFloor: 0.003,
    loudFrames: 0
  };

  function newTurnId() {
    return globalThis.crypto?.randomUUID?.() || `voice-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }

  function appendBounded(target, values, maximum) {
    for (let index = 0; index < values.length; index += 1) target.push(values[index]);
    if (target.length > maximum) target.splice(0, target.length - maximum);
  }

  function rootMeanSquare(values) {
    let energy = 0;
    for (let index = 0; index < values.length; index += 1) energy += values[index] * values[index];
    return Math.sqrt(energy / Math.max(1, values.length));
  }

  function emitVisualLevel() {
    const analyser = capture.analyser;
    if (!analyser) return;
    const timeData = new Uint8Array(analyser.fftSize);
    const frequencyData = new Uint8Array(analyser.frequencyBinCount);
    analyser.getByteTimeDomainData(timeData);
    analyser.getByteFrequencyData(frequencyData);

    let energy = 0;
    for (const value of timeData) {
      const normalized = (value - 128) / 128;
      energy += normalized * normalized;
    }
    const level = Math.max(0, Math.min(1, Math.sqrt(energy / Math.max(1, timeData.length)) * 4.2));
    let peak = 0;
    let peakIndex = 0;
    for (let index = 0; index < frequencyData.length; index += 1) {
      if (frequencyData[index] > peak) {
        peak = frequencyData[index];
        peakIndex = index;
      }
    }
    const dominantHz = peakIndex * (targetSampleRate / analyser.fftSize);
    const frequency = Math.max(0, Math.min(1, dominantHz / 4000));
    globalThis.dispatchEvent(new CustomEvent("go:voice-level", {
      detail: {
        level,
        frequency,
        dominantHz,
        speaking: level >= 0.035,
        active: capture.active && !capture.suspended
      }
    }));
  }

  function startVisualizer() {
    if (capture.visualizerFrame) cancelAnimationFrame(capture.visualizerFrame);
    const draw = () => {
      if (!capture.active) {
        capture.visualizerFrame = 0;
        return;
      }
      emitVisualLevel();
      capture.visualizerFrame = requestAnimationFrame(draw);
    };
    capture.visualizerFrame = requestAnimationFrame(draw);
  }

  function toPcmBase64(samples) {
    const pcm = new Int16Array(samples.length);
    for (let index = 0; index < samples.length; index += 1) {
      const value = Math.max(-1, Math.min(1, samples[index]));
      pcm[index] = value <= -1 ? -32768 : Math.round(value * 32767);
    }
    const bytes = new Uint8Array(pcm.buffer);
    let binary = "";
    const block = 8192;
    for (let offset = 0; offset < bytes.length; offset += block) {
      binary += String.fromCharCode(...bytes.subarray(offset, Math.min(bytes.length, offset + block)));
    }
    return globalThis.btoa(binary);
  }

  function sendWindow(turn, samples, isFinal) {
    if (samples.length < minimumTurnSamples) {
      if (!isFinal) return;
      const padded = samples.slice();
      while (padded.length < minimumTurnSamples) padded.push(0);
      samples = padded;
    }
    globalThis.goBridge.post("microphone.audio", {
      turnId: turn.id,
      chunkIndex: turn.chunkIndex,
      pcm: toPcmBase64(samples),
      isFinal: Boolean(isFinal)
    });
    turn.chunkIndex += 1;
  }

  function emitAvailableWindows(turn) {
    while (turn.samples.length - turn.sentThrough >= chunkStepSamples) {
      const start = Math.max(0, turn.sentThrough - overlapSamples);
      const end = turn.sentThrough + chunkStepSamples;
      sendWindow(turn, turn.samples.slice(start, end), false);
      turn.sentThrough = end;
      if (turn.sentThrough > overlapSamples * 2) {
        const remove = turn.sentThrough - overlapSamples;
        turn.samples.splice(0, remove);
        turn.sentThrough -= remove;
      }
    }
  }

  function finishTurn() {
    const turn = capture.turn;
    capture.turn = null;
    capture.loudFrames = 0;
    capture.preRoll.length = 0;
    if (!turn || turn.speechSamples < minimumTurnSamples) return;
    const start = Math.max(0, turn.sentThrough - overlapSamples);
    sendWindow(turn, turn.samples.slice(start), true);
  }

  function beginTurn(values) {
    const samples = capture.preRoll.slice();
    for (const value of values) samples.push(value);
    capture.preRoll.length = 0;
    capture.turn = {
      id: newTurnId(),
      chunkIndex: 0,
      samples,
      sentThrough: 0,
      silenceSamples: 0,
      speechSamples: values.length
    };
  }

  function consumeSamples(values) {
    if (!capture.active || capture.suspended || !values?.length) return;
    const rms = rootMeanSquare(values);
    const threshold = Math.max(0.0045, capture.noiseFloor * 2.6);
    const loud = rms >= threshold;

    if (!capture.turn) {
      if (!loud) capture.noiseFloor = capture.noiseFloor * 0.985 + rms * 0.015;
      appendBounded(capture.preRoll, values, preRollSamples);
      capture.loudFrames = loud ? capture.loudFrames + 1 : 0;
      if (capture.loudFrames >= 2) beginTurn(values);
      return;
    }

    const turn = capture.turn;
    for (let index = 0; index < values.length; index += 1) turn.samples.push(values[index]);
    if (loud) {
      turn.speechSamples += values.length;
      turn.silenceSamples = 0;
    } else {
      turn.silenceSamples += values.length;
    }
    emitAvailableWindows(turn);
    if (turn.silenceSamples >= silenceToFinishSamples) finishTurn();
  }

  async function start() {
    if (capture.active || capture.starting) {
      const track = capture.stream?.getAudioTracks?.()[0];
      return { deviceLabel: track?.label || "Standardmikrofon", sampleRate: targetSampleRate };
    }
    if (!navigator.mediaDevices?.getUserMedia || !globalThis.AudioContext) {
      throw new Error("Die installierte WebView2-Version unterstützt keine Browser-Mikrofonaufnahme.");
    }

    capture.starting = true;
    try {
      // Match the browser path used by web chat clients: ask Chromium for the
      // default microphone stream and do not alter Windows device processing.
      // Resampling and mono conversion happen only in our AudioWorklet copy.
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
      const track = stream.getAudioTracks()[0];
      const context = new AudioContext({ sampleRate: targetSampleRate, latencyHint: "interactive" });
      await context.audioWorklet.addModule("microphone-worklet.js");
      const source = context.createMediaStreamSource(stream);
      const analyser = context.createAnalyser();
      analyser.fftSize = 128;
      analyser.smoothingTimeConstant = 0.72;
      const processor = new AudioWorkletNode(context, "go-microphone", {
        numberOfInputs: 1,
        numberOfOutputs: 1,
        outputChannelCount: [1]
      });
      // A media-stream sink keeps the worklet pulled without monitoring the
      // microphone through the Windows output device.
      const outputSink = context.createMediaStreamDestination();
      processor.port.onmessage = event => consumeSamples(event.data);
      // Keep capture completely off the output graph. Connecting the microphone
      // to the destination can make Windows switch/attenuate system audio.
      source.connect(analyser);
      analyser.connect(processor);
      processor.connect(outputSink);
      track.addEventListener("ended", () => {
        if (capture.active) globalThis.dispatchEvent(new CustomEvent("go:voice-capture-ended"));
      });
      capture.stream = stream;
      capture.context = context;
      capture.source = source;
      capture.analyser = analyser;
      capture.processor = processor;
      capture.outputSink = outputSink;
      capture.active = true;
      capture.suspended = true;
      capture.preRoll.length = 0;
      capture.turn = null;
      capture.noiseFloor = 0.003;
      capture.loudFrames = 0;
      await context.resume();
      startVisualizer();
      return {
        deviceLabel: track.label || "Standardmikrofon",
        sampleRate: context.sampleRate
      };
    } catch (error) {
      await stop(false);
      throw error;
    } finally {
      capture.starting = false;
    }
  }

  async function stop(flush = true) {
    if (flush && capture.turn) finishTurn();
    capture.active = false;
    capture.suspended = true;
    if (capture.visualizerFrame) cancelAnimationFrame(capture.visualizerFrame);
    capture.visualizerFrame = 0;
    capture.turn = null;
    capture.preRoll.length = 0;
    try { capture.processor?.disconnect(); } catch { }
    try { capture.analyser?.disconnect(); } catch { }
    try { capture.source?.disconnect(); } catch { }
    try { capture.outputSink?.disconnect(); } catch { }
    for (const track of capture.stream?.getTracks?.() || []) track.stop();
    try { await capture.context?.close(); } catch { }
    capture.stream = null;
    capture.context = null;
    capture.source = null;
    capture.analyser = null;
    capture.processor = null;
    capture.outputSink = null;
  }

  function setSuspended(value) {
    const suspended = Boolean(value);
    if (capture.suspended === suspended) return;
    capture.suspended = suspended;
    if (suspended) {
      capture.turn = null;
      capture.preRoll.length = 0;
      capture.loudFrames = 0;
    }
  }

  globalThis.goVoiceCapture = Object.freeze({
    start,
    stop,
    setSuspended,
    get isActive() { return capture.active; },
    get isStarting() { return capture.starting; }
  });
})();
