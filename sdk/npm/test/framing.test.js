'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { METHODS, CAPABILITIES, BASELINE, MessageDecoder, encodeMessage } = require('..');

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

test('exports the persistent state method, capability and baseline answers', () => {
    assert.equal(METHODS.saveState, 'workspace/saveState');
    assert.equal(CAPABILITIES.persistentState, 'persistentState');
    assert.deepEqual({ ...BASELINE }, { accepted: 'accepted', rejected: 'rejected' });
});

test('declares the inputs of a delta with the schema\'s fields', () => {
    const schema = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', '..', 'src', 'CodeContext.Parser.Protocol',
        'protocol', 'parser-protocol.schema.json'), 'utf8')).$defs;
    const declared = /export interface WorkerInputs \{([^}]*)\}/.exec(fs.readFileSync(path.join(__dirname, '..', 'index.d.ts'), 'utf8'));
    assert.ok(declared, 'index.d.ts declares WorkerInputs');
    const fields = [...declared[1].matchAll(/^\s*(\w+)\??:/gm)].map(match => match[1]).sort();
    assert.deepEqual(fields, Object.keys(schema.workerInputs.properties).sort());
    assert.equal(schema.analysisDelta.properties.inputs.$ref, '#/$defs/workerInputs');
});

test('declares the parts of a node with the schema\'s fields', () => {
    const schema = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', '..', 'src', 'CodeContext.Parser.Protocol',
        'protocol', 'parser-protocol.schema.json'), 'utf8')).$defs;
    const declared = /export interface NodePart \{([^}]*)\}/.exec(fs.readFileSync(path.join(__dirname, '..', 'index.d.ts'), 'utf8'));
    assert.ok(declared, 'index.d.ts declares NodePart');
    const fields = [...declared[1].matchAll(/^\s*(\w+)\??:/gm)].map(match => match[1]).sort();
    assert.deepEqual(fields, Object.keys(schema.nodePart.properties).sort());
    assert.equal(schema.node.properties.parts.items.$ref, '#/$defs/nodePart');
});
