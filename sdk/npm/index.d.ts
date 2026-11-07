export const PROTOCOL_VERSION: number;
export const METHODS: Readonly<{
    initialize: string;
    openWorkspace: string;
    indexWorkspace: string;
    applyChanges: string;
    getNativeSyntaxTree: string;
    saveState: string;
    cancel: string;
    analysisDelta: string;
    analysisProgress: string;
    shutdown: string;
}>;

export const CAPABILITIES: Readonly<{
    persistentState: 'persistentState';
}>;
export const BASELINE: Readonly<{
    accepted: 'accepted';
    rejected: 'rejected';
}>;

/** One unit's fingerprint: computed by the worker, stored and handed back by the host, never interpreted by it. */
export interface UnitFingerprint {
    unit: string;
    value: string;
}

/** `workspace/index` params' optional `baseline`, sent only to a worker that declared `persistentState`. */
export interface IndexBaseline {
    statePath: string;
    stateId: string;
    stateSha256: string;
    fingerprints: UnitFingerprint[];
}

/** `workspace/saveState` params. */
export interface SaveStateParams {
    workspaceId: string;
    statePath: string;
    stateId: string;
}

/** `workspace/saveState` result; `bytes` and `sha256` (lowercase hex) are required when `saved`. */
export interface SaveStateResult {
    workspaceId: string;
    stateId: string;
    saved: boolean;
    bytes?: number;
    sha256?: string;
}

export class MessageDecoder {
    constructor(maxContentLength?: number);
    push(chunk: Uint8Array): unknown[];
}

export interface WritableLike {
    write(chunk: Uint8Array, callback: (error?: Error | null) => void): unknown;
}

export function encodeMessage(message: unknown): Uint8Array;
export function writeMessage(stream: WritableLike, message: unknown): Promise<void>;
