// Small injected browser boundaries; feature tests do not boot the application or parse HTML.
export async function withGlobals(values, run) {
  const previous = new Map(Object.keys(values).map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  for (const [name, value] of Object.entries(values)) Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  try { return await run(); }
  finally {
    for (const [name, descriptor] of previous) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
}

export function memoryStorage() {
  const values = new Map();
  return {
    getItem: name => values.get(name) ?? null,
    setItem: (name, value) => values.set(name, String(value)),
    removeItem: name => values.delete(name),
    entries: () => [...values]
  };
}

export function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

export function field(value = '') {
  return { value, handlers: {}, addEventListener(event, handler) { this.handlers[event] = handler; },
    focus() { this.focused = true; }, select() { this.selected = true; } };
}

export class Video extends EventTarget {
  constructor({ nativeHls = false } = {}) {
    super(); Object.assign(this, { nativeHls, muted: true, volume: 1, paused: true, isConnected: true, src: '', currentTime: 0, readyState: 4, dataset: {}, playCalls: 0, pauseCalls: 0, loadCalls: 0 });
  }
  canPlayType() { return this.nativeHls ? 'probably' : ''; }
  async play() { this.playCalls++; this.paused = false; this.dispatchEvent(new Event('playing')); }
  pause() { this.pauseCalls++; this.paused = true; this.dispatchEvent(new Event('pause')); }
  load() { this.loadCalls++; }
  removeAttribute(name) { if (name === 'src') this.src = ''; }
  setAttribute(name, value) { this[name] = String(value); }
  getAttribute(name) { return this[name] ?? null; }
}

export function hlsBoundary() {
  const instances = [];
  class Hls {
    static Events = { ERROR: 'error', MANIFEST_PARSED: 'manifest', FRAG_BUFFERED: 'buffered' };
    static ErrorTypes = { MEDIA_ERROR: 'mediaError', NETWORK_ERROR: 'networkError' };
    static isSupported() { return true; }
    constructor(config) { this.config = config; this.handlers = new Map(); this.sources = []; instances.push(this); }
    on(event, handler) { this.handlers.set(event, handler); }
    emit(event, data) { this.handlers.get(event)?.(event, data); }
    attachMedia(video) { this.video = video; }
    loadSource(url) { this.sources.push(url); }
    startLoad() { this.restarted = true; this.restarts = (this.restarts || 0) + 1; }
    recoverMediaError() { this.recoveries = (this.recoveries || 0) + 1; }
    destroy() { this.destroyed = true; }
  }
  return { Hls, instances };
}

export function timers() {
  let id = 0;
  const intervals = new Map(), timeouts = new Map();
  return { intervals, timeouts,
    setIntervalImpl: callback => { intervals.set(++id, callback); return id; },
    clearIntervalImpl: handle => intervals.delete(handle),
    setTimeoutImpl: callback => { timeouts.set(++id, callback); return id; },
    clearTimeoutImpl: handle => timeouts.delete(handle)
  };
}

export const settle = () => new Promise(resolve => setImmediate(resolve));
