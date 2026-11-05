(function () {
    "use strict";

    let mediaStream = null;
    let audioContext = null;
    let sourceNode = null;
    let processorNode = null;
    let pcmChunks = [];
    let totalSamples = 0;
    let sampleRate = 16000;
    let startTimeMs = 0;
    let progressTimer = null;
    let speechRecognition = null;
    let liveTranscriptText = "";
    let latestRmsLevel = 0;

    function resolveSpeechLocale(langCode) {
        const norm = (langCode || "en").toLowerCase();
        if (norm.startsWith("pl")) return "pl-PL";
        if (norm.startsWith("es")) return "es-ES";
        if (norm.startsWith("fr")) return "fr-FR";
        if (norm.startsWith("de")) return "de-DE";
        return "en-US";
    }

    function downsampleBuffer(buffer, inputSampleRate, targetRate) {
        if (targetRate === inputSampleRate) {
            return new Float32Array(buffer);
        }
        const sampleRateRatio = inputSampleRate / targetRate;
        const newLength = Math.round(buffer.length / sampleRateRatio);
        const result = new Float32Array(newLength);
        let offsetResult = 0;
        let offsetBuffer = 0;
        while (offsetResult < result.length) {
            const nextOffsetBuffer = Math.round((offsetResult + 1) * sampleRateRatio);
            let accum = 0;
            let count = 0;
            for (let i = offsetBuffer; i < nextOffsetBuffer && i < buffer.length; i++) {
                accum += buffer[i];
                count++;
            }
            result[offsetResult] = count > 0 ? accum / count : 0;
            offsetResult++;
            offsetBuffer = nextOffsetBuffer;
        }
        return result;
    }

    function encodeWavBase64(chunks, totalCount, targetSampleRate) {
        const numChannels = 1;
        const bitsPerSample = 16;
        const bytesPerSample = bitsPerSample / 8;
        const blockAlign = numChannels * bytesPerSample;
        const byteRate = targetSampleRate * blockAlign;
        const dataByteLength = totalCount * bytesPerSample;
        const buffer = new ArrayBuffer(44 + dataByteLength);
        const view = new DataView(buffer);

        function writeAscii(offset, str) {
            for (let i = 0; i < str.length; i++) {
                view.setUint8(offset + i, str.charCodeAt(i));
            }
        }

        writeAscii(0, "RIFF");
        view.setUint32(4, 36 + dataByteLength, true);
        writeAscii(8, "WAVE");
        writeAscii(12, "fmt ");
        view.setUint32(16, 16, true); // PCM chunk size
        view.setUint16(20, 1, true);  // AudioFormat = 1 (PCM)
        view.setUint16(22, numChannels, true);
        view.setUint32(24, targetSampleRate, true);
        view.setUint32(28, byteRate, true);
        view.setUint16(32, blockAlign, true);
        view.setUint16(34, bitsPerSample, true);
        writeAscii(36, "data");
        view.setUint32(40, dataByteLength, true);

        let offset = 44;
        for (let c = 0; c < chunks.length; c++) {
            const chunk = chunks[c];
            for (let i = 0; i < chunk.length; i++) {
                const s = Math.max(-1, Math.min(1, chunk[i]));
                const pcm16 = s < 0 ? s * 0x8000 : s * 0x7FFF;
                view.setInt16(offset, pcm16, true);
                offset += 2;
            }
        }

        const bytes = new Uint8Array(buffer);
        let binary = "";
        const step = 0x8000;
        for (let i = 0; i < bytes.length; i += step) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
        }
        return {
            wavBase64: btoa(binary),
            byteLength: bytes.length
        };
    }

    function cleanupAudioGraph() {
        if (progressTimer) {
            clearInterval(progressTimer);
            progressTimer = null;
        }
        if (speechRecognition) {
            try {
                speechRecognition.stop();
            } catch (_) { }
            speechRecognition = null;
        }
        if (processorNode) {
            try {
                processorNode.disconnect();
            } catch (_) { }
            processorNode = null;
        }
        if (sourceNode) {
            try {
                sourceNode.disconnect();
            } catch (_) { }
            sourceNode = null;
        }
        if (mediaStream) {
            try {
                mediaStream.getTracks().forEach(t => t.stop());
            } catch (_) { }
            mediaStream = null;
        }
        if (audioContext) {
            try {
                audioContext.close();
            } catch (_) { }
            audioContext = null;
        }
    }

    window.cgVoiceRecorder = {
        startRecording: async function (dotNetRef, langCode) {
            cleanupAudioGraph();
            pcmChunks = [];
            totalSamples = 0;
            liveTranscriptText = "";
            latestRmsLevel = 0;
            sampleRate = 16000;

            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                return {
                    ok: false,
                    errorMessage: "Browser microphone API (navigator.mediaDevices.getUserMedia) is unavailable in this context."
                };
            }

            try {
                mediaStream = await navigator.mediaDevices.getUserMedia({
                    audio: {
                        echoCancellation: true,
                        noiseSuppression: true,
                        autoGainControl: true,
                        channelCount: 1
                    }
                });

                const AudioCtx = window.AudioContext || window.webkitAudioContext;
                audioContext = new AudioCtx();
                const inputSampleRate = audioContext.sampleRate || 48000;
                sourceNode = audioContext.createMediaStreamSource(mediaStream);
                processorNode = audioContext.createScriptProcessor(4096, 1, 1);

                processorNode.onaudioprocess = function (event) {
                    const inputData = event.inputBuffer.getChannelData(0);
                    let sumSquares = 0;
                    for (let i = 0; i < inputData.length; i++) {
                        sumSquares += inputData[i] * inputData[i];
                    }
                    const rms = Math.sqrt(sumSquares / inputData.length);
                    latestRmsLevel = Math.min(100, Math.round(rms * 350));

                    const downsampled = downsampleBuffer(inputData, inputSampleRate, sampleRate);
                    pcmChunks.push(downsampled);
                    totalSamples += downsampled.length;
                };

                sourceNode.connect(processorNode);
                processorNode.connect(audioContext.destination);

                // Optional browser SpeechRecognition for live interim captions while recording WAV
                const SpeechRec = window.SpeechRecognition || window.webkitSpeechRecognition;
                if (SpeechRec) {
                    try {
                        speechRecognition = new SpeechRec();
                        speechRecognition.lang = resolveSpeechLocale(langCode);
                        speechRecognition.continuous = true;
                        speechRecognition.interimResults = true;
                        speechRecognition.onresult = function (evt) {
                            let full = "";
                            for (let i = 0; i < evt.results.length; i++) {
                                full += evt.results[i][0].transcript + " ";
                            }
                            liveTranscriptText = full.trim();
                        };
                        speechRecognition.start();
                    } catch (_) { }
                }

                startTimeMs = Date.now();
                if (dotNetRef) {
                    progressTimer = setInterval(function () {
                        const elapsedSec = Math.max(0, Math.round((Date.now() - startTimeMs) / 100) / 10);
                        dotNetRef.invokeMethodAsync("OnVoiceRecorderProgress", elapsedSec, latestRmsLevel, liveTranscriptText).catch(function () { });
                    }, 250);
                }

                return {
                    ok: true,
                    errorMessage: null
                };
            } catch (err) {
                cleanupAudioGraph();
                return {
                    ok: false,
                    errorMessage: (err && err.message) ? err.message : "Microphone permission denied or no microphone device found."
                };
            }
        },

        stopRecording: async function () {
            const durationMs = startTimeMs > 0 ? Math.max(100, Date.now() - startTimeMs) : 0;
            const capturedChunks = pcmChunks.slice();
            const capturedSamples = totalSamples;
            const capturedTranscript = (liveTranscriptText || "").trim();

            cleanupAudioGraph();
            pcmChunks = [];
            totalSamples = 0;
            startTimeMs = 0;

            if (capturedSamples <= 0) {
                return {
                    ok: false,
                    wavBase64: "",
                    durationMs: durationMs,
                    byteLength: 0,
                    liveTranscript: capturedTranscript,
                    errorMessage: "No audio frames were captured from the microphone."
                };
            }

            try {
                const encoded = encodeWavBase64(capturedChunks, capturedSamples, sampleRate);
                return {
                    ok: true,
                    wavBase64: encoded.wavBase64,
                    durationMs: durationMs,
                    byteLength: encoded.byteLength,
                    liveTranscript: capturedTranscript,
                    errorMessage: null
                };
            } catch (err) {
                return {
                    ok: false,
                    wavBase64: "",
                    durationMs: durationMs,
                    byteLength: 0,
                    liveTranscript: capturedTranscript,
                    errorMessage: (err && err.message) ? err.message : "Failed to encode WAV buffer."
                };
            }
        },

        cancelRecording: function () {
            cleanupAudioGraph();
            pcmChunks = [];
            totalSamples = 0;
            startTimeMs = 0;
            liveTranscriptText = "";
            return true;
        }
    };
})();

