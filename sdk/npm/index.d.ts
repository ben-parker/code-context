export const PROTOCOL_VERSION: number;
export const METHODS: Readonly<{
    initialize: string;
    openWorkspace: string;
    indexWorkspace: string;
    applyChanges: string;
    getNativeSyntaxTree: string;
    cancel: string;
    analysisDelta: string;
    shutdown: string;
}>;

export class MessageDecoder {
    constructor(maxContentLength?: number);
    push(chunk: Uint8Array): unknown[];
}

export interface WritableLike {
    write(chunk: Uint8Array, callback: (error?: Error | null) => void): unknown;
}

export function encodeMessage(message: unknown): Uint8Array;
export function writeMessage(stream: WritableLike, message: unknown): Promise<void>;
