// Node.js 22+: no external packages. Start the server, then pass its base URL.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function verifyFrontend() {
    const html = readFileSync(new URL('../src/StateSync.Server/wwwroot/index.html', import.meta.url), 'utf8');
    const source = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
    const nodes = new Map();
    const makeNode = () => ({ textContent: '', children: [], disabled: false,
        replaceChildren() { this.children = []; }, appendChild(child) { this.children.push(child); } });
    const buttons = [makeNode(), makeNode()];
    const handlers = {};
    const invokes = [];
    const connection = {
        on(name, cb) { handlers[name] = cb; },
        onreconnecting(cb) { handlers.reconnecting = cb; },
        onclose(cb) { handlers.close = cb; },
        start() { return Promise.resolve(); },
        invoke(...args) { invokes.push(args); return Promise.resolve(); }
    };
    const store = new Map();
    const context = vm.createContext({ console, window: { crypto: {} }, Math, Number, Error,
        sessionStorage: { getItem: key => store.get(key), setItem: (key, val) => store.set(key, val) },
        document: { getElementById(id) { if (!nodes.has(id)) nodes.set(id, makeNode()); return nodes.get(id); },
            createElement: makeNode, querySelectorAll: () => buttons },
        signalR: { HubConnectionBuilder: class {
            withUrl() { return this; } withAutomaticReconnect() { return this; } build() { return connection; }
        } }
    });
    vm.runInContext(source, context);
    assert(buttons.every(b => b.disabled));
    const item = { productId: 'P', name: '<img src=x onerror=alert(1)>', price: 1, quantity: 2 };
    const snapshot = { version: 2, serverLogicalTick: 1000, overheadDisplay: 'N/A',
        lastEvent: '<script>bad()</script>', tombstoneCount: 1, ramKb: 1, crdtItems: [item], baselineItems: [] };
    handlers.StateUpdated(snapshot);
    assert(buttons.every(b => !b.disabled));
    assert.equal(nodes.get('crdtList').children[0].textContent.includes(item.name), true);
    assert.equal(nodes.get('statusBox').textContent.includes(snapshot.lastEvent), true);
    assert.equal(nodes.get('crdtList').innerHTML, undefined); // Payload never reaches an HTML sink.
    vm.runInContext('addItem("P", "Book", 1)', context);
    assert.equal(invokes[0][2], 1010);
    handlers.StateUpdated({ ...snapshot, version: 1, crdtItems: [] });
    assert.equal(nodes.get('crdtList').children[0].textContent.includes(item.name), true);
    handlers.reconnecting();
    assert(buttons.every(b => b.disabled));
    handlers.StateUpdated({ ...snapshot, version: 0, serverLogicalTick: 300 });
    assert(buttons.every(b => !b.disabled));
    console.log('PASS frontend: safe text rendering, stale snapshot, clock, reconnect readiness');
}

const base = (process.argv[2] || 'http://127.0.0.1:5000').replace(/\/$/, '');
const sockets = new Set();
const timeout = (promise, label) => {
    let timer;
    return Promise.race([promise, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`Timeout: ${label}`)), 10000);
    })]).finally(() => clearTimeout(timer));
};

async function connect() {
    const response = await fetch(`${base}/hubs/statesync/negotiate?negotiateVersion=1`, { method: 'POST' });
    assert.equal(response.status, 200);
    const { connectionToken } = await response.json();
    const ws = new WebSocket(`${base.replace(/^http/, 'ws')}/hubs/statesync?id=${encodeURIComponent(connectionToken)}`);
    sockets.add(ws);
    const pending = new Map();
    let nextId = 0, latest;
    let resolveInitial;
    const initial = new Promise(resolve => { resolveInitial = resolve; });
    ws.addEventListener('message', event => {
        for (const raw of event.data.split('\x1e').filter(Boolean)) {
            const frame = JSON.parse(raw);
            if (frame.type === 1 && frame.target === 'StateUpdated') {
                latest = frame.arguments[0];
                resolveInitial(latest);
            }
            if (frame.type === 3) {
                const call = pending.get(frame.invocationId);
                if (call) { pending.delete(frame.invocationId); frame.error ? call.reject(new Error(frame.error)) : call.resolve(); }
            }
        }
    });
    await timeout(new Promise((resolve, reject) => {
        ws.addEventListener('open', resolve, { once: true });
        ws.addEventListener('error', reject, { once: true });
    }), 'WebSocket open');
    ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + '\x1e');
    await timeout(initial, 'initial snapshot');
    return {
        get latest() { return latest; },
        invoke(target, ...args) {
            const invocationId = String(++nextId);
            const result = new Promise((resolve, reject) => pending.set(invocationId, { resolve, reject }));
            ws.send(JSON.stringify({ type: 1, invocationId, target, arguments: args }) + '\x1e');
            return timeout(result, target);
        },
        async close() {
            const closed = new Promise(resolve => ws.addEventListener('close', resolve, { once: true }));
            ws.close();
            await timeout(closed, 'disconnect');
            sockets.delete(ws);
        }
    };
}

const status = async () => {
    const response = await fetch(`${base}/api/status`);
    assert.equal(response.status, 200);
    return response.json();
};
const contains = (state, id) => state.crdtActiveItems.some(i => i.productId === id);

try {
    verifyFrontend();
    assert.equal((await fetch(base)).status, 200);
    let a = await connect();
    const b = await connect();
    const runId = `verify_${Date.now()}`;
    const item = { productId: runId, name: 'Reconnect replay', price: 10, quantity: 1 };
    const tick = Math.max(a.latest.serverLogicalTick, b.latest.serverLogicalTick) + 100;
    await a.invoke('BroadcastAdd', item, tick, runId);
    await a.invoke('BroadcastRemove', item, tick + 10, runId);
    await a.invoke('BroadcastAdd', item, tick, runId);
    assert.equal(contains(await status(), item.productId), false);
    await a.close();
    a = await connect();
    await a.invoke('BroadcastAdd', item, tick, runId);
    assert.equal(contains(await status(), item.productId), false);
    await a.invoke('RunGarbageCollection');
    await a.close();
    a = await connect();
    await a.invoke('BroadcastAdd', item, tick, runId);
    assert.equal(contains(await status(), item.productId), false);
    await a.invoke('BroadcastAdd', { ...item, quantity: 5 }, tick + 20, runId);
    await a.invoke('BroadcastRemove', item, tick + 10, runId);
    assert.equal((await status()).crdtActiveItems.find(i => i.productId === runId).quantity, 5);
    console.log('PASS SignalR: duplicate Add/Remove on same connection, reconnect, after GC; new payload update');

    const delayed = { ...item, productId: runId + '_delayed' };
    await b.invoke('BroadcastRemove', delayed, tick + 40, runId + '_b');
    await b.invoke('BroadcastAdd', delayed, tick + 30, runId + '_b');
    assert.equal(contains(await status(), delayed.productId), false);
    const beforeInvalid = await status();
    await assert.rejects(a.invoke('BroadcastAdd', { ...item, productId: null }, tick + 50, runId));
    await assert.rejects(a.invoke('BroadcastAdd', item, -1, runId));
    assert.equal((await status()).version, beforeInvalid.version);
    await Promise.all(Array.from({ length: 20 }, (_, i) => (i % 2 ? a : b).invoke('BroadcastAdd',
        { ...item, productId: `${runId}_parallel_${i}` }, tick + 100 + i, `${runId}_client_${i}`)));
    assert.equal((await status()).crdtActiveItems.filter(i => i.productId.startsWith(runId + '_parallel_')).length, 20);
    await a.invoke('SimulateOutOfOrderAnomaly');
    assert.equal(contains(await status(), 'BOOK_DP_01'), false);
    await a.invoke('RunGarbageCollection');
    await a.invoke('SimulateOutOfOrderAnomaly');
    const final = await status();
    assert.equal(contains(final, 'BOOK_DP_01'), false);
    assert(final.baselineActiveItems.some(i => i.productId === 'BOOK_DP_01'));
    console.log('PASS SignalR: remove-before-add, invalid inputs, 20 concurrent operations, repeated anomaly after GC');
    await a.close(); await b.close();
} finally {
    for (const ws of sockets) ws.close();
}
