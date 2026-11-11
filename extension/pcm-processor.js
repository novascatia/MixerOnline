/**
 * MixerOnline Studio HD PCM AudioWorklet Processor
 * Captures bit-exact 32-bit IEEE Float stereo samples at 48,000 Hz.
 * Dispatches every 128 frames (2.67ms) for zero-lag instant transmission.
 */
class PcmProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.framesPerPacket = 128; // 2.67ms instant dispatch
    this.buffer = new Float32Array(this.framesPerPacket * 2);
    this.frameIndex = 0;
  }

  process(inputs, outputs, parameters) {
    const input = inputs[0];
    if (!input || input.length === 0 || !input[0]) {
      return true;
    }

    const left = input[0];
    const right = input.length > 1 && input[1] ? input[1] : left;
    const numFrames = left.length;

    for (let i = 0; i < numFrames; i++) {
      const dst = this.frameIndex * 2;
      this.buffer[dst] = left[i];
      this.buffer[dst + 1] = right[i];
      this.frameIndex++;

      if (this.frameIndex >= this.framesPerPacket) {
        const packet = new Float32Array(this.buffer);
        this.port.postMessage(packet.buffer, [packet.buffer]);
        this.frameIndex = 0;
      }
    }

    return true;
  }
}

registerProcessor('pcm-processor', PcmProcessor);
