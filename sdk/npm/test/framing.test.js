'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { METHODS, MessageDecoder, encodeMessage } = require('..');

test('decodes fragmented and adjacent frames', () => {
    const first = encodeMessage({ jsonrpc: '2.0', id: 1, result: 'héllo' });
    const second = encodeMessage({ jsonrpc: '2.0', method: 'shutdown' });
    const bytes = Buffer.concat([first, second]);
    const decoder = new MessageDecoder();

    assert.deepEqual(decoder.push(bytes.subarray(0, 9)), []);
    assert.deepEqual(decoder.push(bytes.subarray(9)), [
        { jsonrpc: '2.0', id: 1, result: 'héllo' },
        { jsonrpc: '2.0', method: 'shutdown' },
    ]);
});

test('exports analysis progress notification method', () => {
    assert.equal(METHODS.analysisProgress, 'analysis/progress');
});
