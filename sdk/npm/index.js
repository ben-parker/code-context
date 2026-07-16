'use strict';

const PROTOCOL_VERSION = 1;
const METHODS = Object.freeze({
    initialize: 'initialize',
    openWorkspace: 'workspace/open',
    indexWorkspace: 'workspace/index',
    applyChanges: 'workspace/applyChanges',
    getNativeSyntaxTree: 'syntaxTree/get',
    cancel: '$/cancel',
    analysisDelta: 'analysis/delta',
    shutdown: 'shutdown',
});

function encodeMessage(message) {
    const payload = Buffer.from(JSON.stringify(message), 'utf8');
    return Buffer.concat([
        Buffer.from(`Content-Length: ${payload.length}\r\n\r\n`, 'ascii'),
        payload,
    ]);
}

class MessageDecoder {
    constructor(maxContentLength = 16 * 1024 * 1024) {
        this.maxContentLength = maxContentLength;
        this.buffer = Buffer.alloc(0);
    }

    push(chunk) {
        this.buffer = Buffer.concat([this.buffer, Buffer.from(chunk)]);
        const messages = [];
        while (true) {
            const headerEnd = this.buffer.indexOf('\r\n\r\n');
            if (headerEnd < 0) return messages;
            const header = this.buffer.subarray(0, headerEnd).toString('ascii');
            const match = /(?:^|\r\n)Content-Length:\s*(\d+)\s*(?:\r\n|$)/i.exec(header);
            if (!match) throw new Error('Frame is missing Content-Length.');
            const length = Number(match[1]);
            if (!Number.isSafeInteger(length) || length < 0 || length > this.maxContentLength) {
                throw new Error(`Invalid Content-Length: ${match[1]}.`);
            }
            const frameEnd = headerEnd + 4 + length;
            if (this.buffer.length < frameEnd) return messages;
            const payload = this.buffer.subarray(headerEnd + 4, frameEnd);
            this.buffer = this.buffer.subarray(frameEnd);
            messages.push(JSON.parse(payload.toString('utf8')));
        }
    }
}

function writeMessage(stream, message) {
    return new Promise((resolve, reject) => {
        stream.write(encodeMessage(message), error => error ? reject(error) : resolve());
    });
}

module.exports = { PROTOCOL_VERSION, METHODS, MessageDecoder, encodeMessage, writeMessage };
