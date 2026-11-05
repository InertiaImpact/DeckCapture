import assert from 'node:assert/strict';
import test from 'node:test';
import * as api from '../src/Supervisor/wwwroot/api.js';
import { deferred, memoryStorage, withGlobals } from './helpers/browser.mjs';

async function exerciseMutationHeaders(cryptoStub) {
  const calls = [];
  return withGlobals({ crypto: cryptoStub, sessionStorage: memoryStorage(), fetch: async (url, options) => {
    calls.push({ url, options });
    return { ok: true, status: 200, statusText: 'OK', text: async () => '{"accepted":true}' };
  } }, async () => {
    api.setToken('stub-token');
    assert.deepEqual(await api.post('/sample', { action: 'create' }), { accepted: true });
    assert.deepEqual(await api.put('/sample', { action: 'update' }), { accepted: true });
    return calls;
  });
}

test('POST and PUT have unique valid idempotency keys with randomUUID', async () => {
  let n = 0;
  const calls = await exerciseMutationHeaders({ randomUUID: () => `secure-key-${++n}-000000000000` });
  assertMutationHeaders(calls, ['POST', 'PUT']);
});

test('POST and PUT have unique valid idempotency keys without randomUUID', async () => {
  let n = 0;
  const calls = await exerciseMutationHeaders({
    getRandomValues(bytes) { bytes.fill(0); bytes[0] = ++n; return bytes; }
  });
  assertMutationHeaders(calls, ['POST', 'PUT']);
});

function assertMutationHeaders(calls, expectedMethods) {
  assert.equal(calls.length, expectedMethods.length);
  const keys = calls.map(({ options }) => options.headers.get('Idempotency-Key'));
  assert.deepEqual(calls.map(({ options }) => options.method), expectedMethods);
  assert.equal(new Set(keys).size, calls.length, 'mutation keys must be unique');
  for (const key of keys) assert.match(key, /^[A-Za-z0-9_.-]{8,128}$/);
  for (const { options } of calls) {
    assert.equal(options.headers.get('Authorization'), 'Bearer stub-token');
    assert.equal(options.headers.get('Content-Type'), 'application/json');
  }
}

test('mutations fail before network when no secure random source exists', async () => {
  let calls = 0;
  await withGlobals({ crypto: {}, sessionStorage: memoryStorage(), fetch: async () => { calls++; } }, async () => {
    await assert.rejects(api.post('/sample', {}), /secure random/i);
    assert.equal(calls, 0);
  });
});

test('token-authenticated state events use a bearer header rather than a URL credential', { timeout: 2000 }, async () => {
  const calls = [], received = deferred(), finish = deferred();
  let first = true;
  const storage = memoryStorage();
  await withGlobals({ sessionStorage: storage, EventSource: class { constructor() { throw Error('Token must not enter EventSource URLs'); } },
    fetch: async (url, options) => {
      calls.push({ url, options });
      return { ok: true, body: { getReader: () => ({ read: () => {
        if (first) { first = false; return Promise.resolve({ value: new TextEncoder().encode('event: state\ndata: {"channels":[]}\n\n'), done: false }); }
        return finish.promise;
      } }) } };
    }
  }, async () => {
    api.setToken('unit-event-token');
    const stop = await api.subscribe(value => received.resolve(value), error => received.reject(error));
    try {
      assert.deepEqual(await received.promise, { channels: [] });
      assert.equal(calls[0].url, '/api/events');
      assert.equal(calls[0].options.headers.get('Authorization'), 'Bearer unit-event-token');
      assert.equal(calls[0].options.headers.get('Accept'), 'text/event-stream');
    } finally {
      stop(); finish.resolve({ done: true });
      await new Promise(resolve => setImmediate(resolve));
    }
    assert.equal(calls[0].options.signal.aborted, true);
    assert.equal(calls.length, 1, 'closed subscriptions must not reconnect');
  });
});

test('an unauthenticated event subscription has no token query and closes its native source', async () => {
  let source;
  class Events {
    constructor(url) { source = this; this.url = url; this.handlers = {}; }
    addEventListener(name, callback) { this.handlers[name] = callback; }
    close() { this.closed = true; }
  }
  await withGlobals({ sessionStorage: memoryStorage(), EventSource: Events }, async () => {
    const updates = [];
    const stop = await api.subscribe(value => updates.push(value), error => { throw error; });
    source.handlers.state({ data: '{"channels":[]}' });
    assert.equal(source.url, '/api/events');
    assert.deepEqual(updates, [{ channels: [] }]);
    stop(); assert.equal(source.closed, true);
  });
});
