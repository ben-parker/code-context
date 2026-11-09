#!/usr/bin/env node
'use strict';

// CodeContext TypeScript/JavaScript language worker.
//
// A persistent child process of the CodeContext host: speaks JSON-RPC 2.0 with
// Content-Length framing over stdin/stdout (protocol/parser-protocol.schema.json),
// opens no ports, and exits when stdin reaches EOF — the mandatory self-cleaning
// signal if the host dies. It replaces the old process-per-file bridge: one
// ts.LanguageService holds the project state, so a one-file edit re-parses one
// snapshot instead of respawning Node and re-reading the world.
//
// Project ownership / module resolution: the host approves the file set (one
// workspace per watched root for now). If <root>/tsconfig.json (or, without one,
// jsconfig.json) exists its compilerOptions drive module resolution; otherwise
// permissive defaults (allowJs, esnext) are used. An import alias is tried first
// against the `paths` of the tsconfig.json or jsconfig.json nearest the importing
// file, then the root config, then the configs the root one references and a
// generated .nuxt/tsconfig.json. Approved files are the program roots —
// tsconfig include/exclude does not override the host's approved set.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

let ts;
let parseVueSfc;
try {
    ts = require('typescript');
    ({ parse: parseVueSfc } = require('@vue/compiler-sfc'));
} catch (error) {
    process.stderr.write(
        'typescript-worker: cannot load its TypeScript/Vue compiler dependencies. ' +
        'Run "npm install" next to typescript-worker.js.\n');
    process.exit(2);
}

const PROTOCOL_VERSION = 1;
const PARSER_ID = 'typescript';
const SOURCE_EXTENSIONS = ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs', '.vue'];
const PARSER_VERSION = (() => {
    try {
        return require(path.join(__dirname, 'package.json')).version || '1.0.0';
    } catch {
        return '1.0.0';
    }
})();
// Matches the C# worker's chunk size so both language pipelines have the same
// streaming granularity.
const MAX_ITEMS_PER_DELTA = 2000;

const ErrorCodes = {
    InvalidParams: -32602,
    InternalError: -32603,
    MethodNotFound: -32601,
    RequestCancelled: -32800,
};

// ---------------------------------------------------------------------------
// JSON-RPC endpoint (Content-Length framing, sequential request processing,
// out-of-band $/cancel)
// ---------------------------------------------------------------------------

const cancellations = new Map(); // requestId -> { cancelled: boolean }

/** Resolves when the frame has been handed to the OS pipe: awaited on the exit
 * path (and between delta chunks) so backpressured frames are never truncated
 * by process.exit. */
let lastWrite = Promise.resolve();

function writeMessage(message) {
    const payload = Buffer.from(JSON.stringify(message), 'utf8');
    const header = Buffer.from(`Content-Length: ${payload.length}\r\n\r\n`, 'ascii');
    const written = new Promise(resolve =>
        process.stdout.write(Buffer.concat([header, payload]), resolve));
    lastWrite = written;
    return written;
}

function respondResult(id, result) {
    writeMessage({ jsonrpc: '2.0', id, result: result === undefined ? null : result });
}

function respondError(id, code, message) {
    writeMessage({ jsonrpc: '2.0', id, error: { code, message } });
}

function notify(method, params) {
    return writeMessage({ jsonrpc: '2.0', method, params });
}

/** Yields to the event loop so queued stdin data ($/cancel) gets dispatched. */
function breathe() {
    return new Promise(resolve => setImmediate(resolve));
}

function throwIfCancelled(token) {
    if (token && token.cancelled) {
        const error = new Error('The request was cancelled.');
        error.rpcCode = ErrorCodes.RequestCancelled;
        throw error;
    }
}

const handlers = new Map();
let requestChain = Promise.resolve();

function dispatch(message) {
    if (!message || message.jsonrpc !== '2.0') {
        return;
    }

    if (message.method === '$/cancel') {
        const token = message.params && cancellations.get(message.params.requestId);
        if (token) token.cancelled = true;
        return;
    }

    if (message.id === undefined || message.id === null) {
        return; // unknown notification
    }

    const handler = handlers.get(message.method);
    if (!handler) {
        respondError(message.id, ErrorCodes.MethodNotFound, `Method '${message.method}' is not supported.`);
        return;
    }

    const token = { cancelled: false };
    cancellations.set(message.id, token);

    // Requests run strictly in order (workspace mutations arrive pre-ordered from
    // the host coordinator); only $/cancel is handled out of band above.
    requestChain = requestChain.then(async () => {
        try {
            const result = await handler(message.id, message.params, token);
            respondResult(message.id, result);
        } catch (error) {
            respondError(
                message.id,
                error.rpcCode || ErrorCodes.InternalError,
                error.message || String(error));
        } finally {
            cancellations.delete(message.id);
        }
    });
}

function startReadLoop() {
    let buffer = Buffer.alloc(0);

    process.stdin.on('data', chunk => {
        buffer = Buffer.concat([buffer, chunk]);
        while (true) {
            const headerEnd = buffer.indexOf('\r\n\r\n');
            if (headerEnd < 0) return;
            const header = buffer.slice(0, headerEnd).toString('ascii');
            const match = /content-length:\s*(\d+)/i.exec(header);
            if (!match) {
                process.stderr.write('typescript-worker: frame without Content-Length; exiting.\n');
                process.exit(1);
            }
            const length = parseInt(match[1], 10);
            const frameEnd = headerEnd + 4 + length;
            if (buffer.length < frameEnd) return;
            const payload = buffer.slice(headerEnd + 4, frameEnd).toString('utf8');
            buffer = buffer.slice(frameEnd);
            let message;
            try {
                message = JSON.parse(payload);
            } catch {
                process.stderr.write('typescript-worker: unparseable JSON-RPC payload; exiting.\n');
                process.exit(1);
            }
            dispatch(message);
        }
    });

    // stdin EOF is the mandatory exit signal: finish in-flight work, drain the
    // last stdout frame, then leave.
    process.stdin.on('end', () => {
        requestChain.then(() => lastWrite).finally(() => process.exit(0));
    });
    process.stdin.on('error', () => process.exit(0));
    process.stdout.on('error', () => process.exit(0));
}

// ---------------------------------------------------------------------------
// Workspace state: one ts.LanguageService per workspace
// ---------------------------------------------------------------------------

let hostRootPath = process.cwd();
const workspaces = new Map(); // workspaceId -> Workspace

/**
 * Canonical map key for a path. TypeScript's language service calls host
 * callbacks with ITS normalization of file names (forward slashes, original
 * casing), so every lookup must go through the same canonical form — otherwise
 * getScriptVersion silently answers "0" forever and edits are never re-read.
 */
function canonicalKey(p) {
    const resolved = path.resolve(p);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

// ---------------------------------------------------------------------------
// Embedded-language adapters
// ---------------------------------------------------------------------------

/** Replaces non-newline characters with spaces so embedded code retains the physical
 * file's offsets, line numbers, and columns when TypeScript parses it. */
function sourceShapedWhitespace(source) {
    return source.replace(/[^\r\n]/g, ' ');
}

function scriptKindForLanguage(language) {
    switch ((language || 'js').toLowerCase()) {
        case 'ts': return ts.ScriptKind.TS;
        case 'tsx': return ts.ScriptKind.TSX;
        case 'jsx': return ts.ScriptKind.JSX;
        case 'js': return ts.ScriptKind.JS;
        default: return undefined;
    }
}

function vueErrorMessage(error) {
    if (typeof error === 'string') return error;
    return error?.message || String(error);
}

// The @vue/compiler-core node kinds and element tag type a template walk reads.
const VUE_ELEMENT = 1;
const VUE_INTERPOLATION = 5;
const VUE_DIRECTIVE = 7;
const VUE_COMPONENT_TAG = 1;

const BARE_IDENTIFIER = /^[A-Za-z_$][\w$]*$/;
const IDENTIFIERS = /[A-Za-z_$][\w$]*/g;

/** The script bindings a component tag can name, in the order Vue tries them:
 * `<download-queue>` renders `downloadQueue` or `DownloadQueue` as readily as itself. */
function componentBindingNames(tag) {
    const camel = tag.replace(/-(\w)/g, (_, letter) => letter.toUpperCase());
    return [...new Set([tag, camel, camel.charAt(0).toUpperCase() + camel.slice(1)])];
}

/**
 * Reads what a Vue template uses from its script. A component tag and an event handler
 * given by name are returned as references for the analysis to bind by name; every other
 * expression is returned by its source range, to be parsed where it stands. A name that
 * a `v-for` or `v-slot` declares hides the script's on that element and below it: no
 * reference is made to it, and each expression carries the names hidden where it stands.
 */
function collectVueTemplateUsage(root) {
    const references = []; // { names, display, offset, component }
    const expressions = []; // { start, end, statements, hidden }
    const addExpression = (expression, statements, hidden) => {
        if (!expression?.loc || !expression.content?.trim()) return;
        expressions.push({
            start: expression.loc.start.offset, end: expression.loc.end.offset, statements, hidden,
        });
    };
    const walk = (node, outer) => {
        let hidden = outer;
        if (node.type === VUE_ELEMENT) {
            const directives = (node.props ?? []).filter(prop => prop.type === VUE_DIRECTIVE);
            const declared = directives
                .flatMap(prop => {
                    if (prop.name === 'slot') return [prop.exp];
                    if (prop.name !== 'for') return [];
                    const parsed = prop.forParseResult;
                    return parsed ? [parsed.value, parsed.key, parsed.index] : [prop.exp];
                })
                .flatMap(expression => expression?.content?.match(IDENTIFIERS) ?? []);
            if (declared.length > 0) hidden = new Set([...outer, ...declared]);
            const addReference = (names, display, offset, component) => {
                if (!names.some(name => hidden.has(name))) references.push({ names, display, offset, component });
            };

            if (node.tagType === VUE_COMPONENT_TAG && node.tag !== 'component' && !node.tag.includes('.')) {
                addReference(componentBindingNames(node.tag), node.tag, node.loc.start.offset + 1, true);
            }
            for (const prop of directives) {
                if (prop.name === 'slot') continue;
                const name = prop.exp?.content?.trim();
                const named = name && BARE_IDENTIFIER.test(name);
                if (named && prop.name === 'on' && prop.arg) {
                    // `@click="save"` hands Vue the function to call on the event.
                    addReference([name], name, prop.exp.loc.start.offset, false);
                } else if (named && prop.name === 'bind' && node.tag === 'component'
                    && prop.arg?.isStatic && prop.arg.content === 'is') {
                    addReference([name], name, prop.exp.loc.start.offset, true);
                } else if (prop.name === 'for') {
                    // Only the iterated source is an expression, read before the loop's
                    // own names exist; the rest declares them.
                    addExpression(prop.forParseResult?.source, false, outer);
                } else {
                    // An event handler that is not a name is a statement list.
                    addExpression(prop.exp, prop.name === 'on' && !!prop.arg, hidden);
                }
            }
        } else if (node.type === VUE_INTERPOLATION) {
            addExpression(node.content, false, hidden);
        }
        for (const child of node.children ?? []) {
            if (child && typeof child === 'object') walk(child, hidden);
        }
    };
    walk(root, new Set());
    return { references, expressions };
}

// Syntax the analysis emits a node for. A template expression holding any of it would
// add nodes the script never declared, so such an expression is not copied.
const DECLARING_KINDS = new Set([
    ts.SyntaxKind.FunctionDeclaration, ts.SyntaxKind.ClassDeclaration, ts.SyntaxKind.ClassExpression,
    ts.SyntaxKind.InterfaceDeclaration, ts.SyntaxKind.TypeAliasDeclaration, ts.SyntaxKind.EnumDeclaration,
    ts.SyntaxKind.ModuleDeclaration, ts.SyntaxKind.VariableStatement, ts.SyntaxKind.MethodDeclaration,
    ts.SyntaxKind.GetAccessor, ts.SyntaxKind.SetAccessor, ts.SyntaxKind.ImportDeclaration,
    ts.SyntaxKind.ExportDeclaration, ts.SyntaxKind.ExportAssignment,
]);

/**
 * True when `code`, a wrapped template expression, can stand in the script's text: it
 * parses (an unterminated string or bracket would swallow the script), declares
 * nothing, keeps its line breaks where the file has them, and calls nothing through a
 * name the template has hidden, which would bind to the script's declaration instead.
 */
function isSafeTemplateStatement(code, scriptKind, hidden) {
    if (/[\u2028\u2029]/.test(code)) return false;
    const parsed = ts.createSourceFile('template-expression', code, ts.ScriptTarget.Latest, false, scriptKind);
    if (!Array.isArray(parsed.parseDiagnostics) || parsed.parseDiagnostics.length > 0) return false;
    const safe = node => {
        if (DECLARING_KINDS.has(node.kind)) return false;
        if (hidden.size > 0 && (ts.isCallExpression(node) || ts.isNewExpression(node))) {
            let callee = node.expression;
            while (callee && !ts.isIdentifier(callee)) callee = callee.expression;
            if (callee && hidden.has(callee.text)) return false;
        }
        return !ts.forEachChild(node, child => !safe(child));
    };
    return safe(parsed);
}

/** The two nearest offsets from `from` towards `bound` (exclusive) that hold no line
 * break, or undefined. Overwriting one keeps every line where the physical file has it. */
function spareOffsets(source, from, bound, step) {
    const found = [];
    for (let offset = from; step > 0 ? offset < bound : offset > bound; offset += step) {
        const ch = source.charCodeAt(offset);
        if (ch === 10 || ch === 13) continue;
        found.push(offset);
        if (found.length === 2) return found;
    }
    return undefined;
}

/**
 * Copies each template expression into the whitespace-shaped text at its own offsets, as
 * a statement of its own: `;(expr);`, or `;{statements};` for an inline event handler.
 * The delimiters overwrite markup next to the expression, so a call inside it is parsed,
 * bound against the script's module scope, and reported at its line in the template. An
 * expression that is not safe to copy (see isSafeTemplateStatement), or has no markup to
 * spare for its delimiters, is left out.
 */
function overlayTemplateExpressions(text, source, expressions, contentStart, contentEnd, scriptKind) {
    const ordered = [...expressions].sort((a, b) => a.start - b.start);
    const pieces = [];
    let position = 0;
    let floor = contentStart;
    ordered.forEach((expression, index) => {
        const body = source.substring(expression.start, expression.end);
        const [open, close] = expression.statements ? ['{', '}'] : ['(', ')'];
        if (expression.start < floor
            || !isSafeTemplateStatement(`;${open}${body}${close};`, scriptKind, expression.hidden)) {
            return;
        }
        const ceiling = Math.min(contentEnd, ordered[index + 1]?.start ?? contentEnd);
        const before = spareOffsets(source, expression.start - 1, floor - 1, -1);
        const after = spareOffsets(source, expression.end, ceiling, 1);
        if (!before || !after) return;
        pieces.push(
            text.substring(position, before[1]), ';',
            text.substring(before[1] + 1, before[0]), open,
            text.substring(before[0] + 1, expression.start), body,
            text.substring(expression.end, after[0]), close,
            text.substring(after[0] + 1, after[1]), ';');
        position = after[1] + 1;
        floor = position;
    });
    return pieces.join('') + text.substring(position);
}

const embeddedLanguageAdapters = new Map([
    ['.vue', {
        transform(fileName, source) {
            const parsed = parseVueSfc(source, { filename: fileName, sourceMap: false });
            const blocks = [parsed.descriptor.script, parsed.descriptor.scriptSetup].filter(Boolean);
            let text = sourceShapedWhitespace(source);
            let scriptKind = ts.ScriptKind.JS;
            let scripted = false;
            let isModule = false;
            const diagnostics = parsed.errors.map(error => ({
                filePath: fileName,
                severity: 'warning',
                message: `Vue SFC parse error: ${vueErrorMessage(error)}`,
            }));

            for (const block of blocks) {
                const blockKind = scriptKindForLanguage(block.lang);
                if (blockKind === undefined) {
                    diagnostics.push({
                        filePath: fileName,
                        severity: 'warning',
                        message: `Vue <script> language '${block.lang}' is not supported by the TypeScript worker.`,
                    });
                    continue;
                }
                // Prefer the most expressive grammar when an SFC has both script blocks.
                if (blockKind === ts.ScriptKind.TSX || blockKind === ts.ScriptKind.TS
                    || (blockKind === ts.ScriptKind.JSX && scriptKind === ts.ScriptKind.JS)) {
                    scriptKind = blockKind;
                }
                const start = block.loc.start.offset;
                text = text.substring(0, start) + block.content + text.substring(start + block.content.length);
                if (block.src) continue; // its code is in another file
                scripted = true;

                // Vue compiles every SFC script as an ES module. One with no import or
                // export is a global script to TypeScript, where its names would merge
                // with other components' and with the DOM's. `export{}` written over
                // the opening `<script` tag says what Vue does, unless the script is
                // CommonJS, which TypeScript already scopes to its file.
                let tagStart = -1;
                for (const tag of source.matchAll(/<script\b/gi)) {
                    if (tag.index >= start) break;
                    tagStart = tag.index;
                }
                if (!isModule && tagStart >= 0 && start - tagStart >= 8
                    && !/\bmodule\s*\.\s*exports\b|\bexports\s*\./.test(block.content)) {
                    text = text.substring(0, tagStart) + 'export{'
                        + text.substring(tagStart + 7, start - 1) + '}' + text.substring(start);
                    isModule = true;
                }
            }

            // A template uses what a script declares or imports, and the components the
            // project declares for every template; without a script it has only those.
            // One written in another language (pug) or kept in another file has no AST here.
            let templateReferences = [];
            let templateHidden = [];
            const template = parsed.descriptor.template;
            if (template?.ast) {
                const usage = collectVueTemplateUsage(template.ast);
                templateReferences = scripted
                    ? usage.references
                    : usage.references.filter(reference => reference.component);
                if (scripted) {
                    templateHidden = usage.expressions.filter(expression => expression.hidden.size > 0);
                    text = overlayTemplateExpressions(text, source, usage.expressions,
                        template.loc.start.offset, template.loc.end.offset, scriptKind);
                }
            }

            return { text, scriptKind, diagnostics, templateReferences, templateHidden };
        },
    }],
]);

class Workspace {
    constructor(rootPath) {
        this.rootPath = rootPath;
        this.files = new Map(); // canonicalKey -> { path, version }
        this.embeddedSources = new Map(); // canonicalKey -> { version, text, scriptKind, diagnostics, ... }
        // What each file's imports resolved to when the program was last built, so the
        // analysis can name an import's target file without resolving it a second time.
        this.moduleTargets = new Map(); // canonicalKey -> Map(moduleName -> resolvedFileName)
        // Per-file content hash of the facts last emitted for that file, keyed by
        // canonicalKey (so lookups survive Windows casing) with the verbatim bucket path
        // stored alongside. This is the sole record of "what the host currently holds per
        // file": workspace/index reseeds it wholesale; each incremental applyChanges reads
        // it to find the dirty buckets and commits the new hashes back. Its key set
        // therefore equals the set of files whose facts are committed downstream, so a key
        // present here with no current bucket is a removal — no separate drop-tracking is
        // needed (mirrors the C# analyzer's _factHashes / PathComparer semantics).
        this.factHashes = new Map(); // canonicalKey -> { hash: string, path: string }
        this.applyConfiguration(loadCompilerOptions(rootPath));
        const self = this;
        this.serviceHost = {
            getScriptFileNames: () => [...self.files.values()].map(f => f.path),
            getScriptVersion: fileName => String(self.files.get(canonicalKey(fileName))?.version ?? 0),
            getScriptSnapshot: fileName => {
                const source = self.getScriptSource(fileName);
                return source ? ts.ScriptSnapshot.fromString(source.text) : undefined;
            },
            getScriptKind: fileName => self.getScriptSource(fileName)?.scriptKind
                ?? ts.getScriptKindFromFileName(fileName),
            getCurrentDirectory: () => self.rootPath,
            getCompilationSettings: () => self.compilerOptions,
            getDefaultLibFileName: options => ts.getDefaultLibFilePath(options),
            fileExists: fileName => self.hasFile(fileName) || ts.sys.fileExists(fileName),
            readFile: fileName => self.getScriptSource(fileName)?.text ?? ts.sys.readFile(fileName),
            readDirectory: ts.sys.readDirectory,
            directoryExists: ts.sys.directoryExists,
            getDirectories: ts.sys.getDirectories,
            resolveModuleNames: (moduleNames, containingFile) => {
                // Library files resolve imports too; only the workspace's are asked about.
                const key = canonicalKey(containingFile);
                let targets = self.moduleTargets.get(key);
                if (!targets && self.files.has(key)) {
                    targets = new Map();
                    self.moduleTargets.set(key, targets);
                }
                return moduleNames.map(moduleName => {
                    const resolved = self.resolveModule(moduleName, containingFile);
                    targets?.set(moduleName, resolved?.resolvedFileName);
                    return resolved;
                });
            },
        };
        this.service = ts.createLanguageService(this.serviceHost, ts.createDocumentRegistry());
    }

    applyConfiguration(loaded) {
        this.compilerOptions = loaded.options;
        this.configDiagnostic = loaded.diagnostic;
        // Each scope resolves with its own config's `paths`, relative to that config.
        this.aliasScopes = loaded.aliasScopes.map(scope => this.withOptions(scope));
        this.nestedScopes = new Map();
        this.directoryScopes = new Map();
        this.directoryComponents = new Map();
        this.componentDeclarations = new Map();
    }

    withOptions(scope) {
        const { paths, baseUrl, pathsBasePath } = scope;
        return { ...scope, options: { ...this.compilerOptions, paths, baseUrl, pathsBasePath } };
    }

    /** The alias scope of the tsconfig.json or jsconfig.json in `directory`, a directory
     * below the root; undefined when it has none or the config declares no aliases. */
    nestedScope(directory) {
        const key = canonicalKey(directory);
        if (this.nestedScopes.has(key)) return this.nestedScopes.get(key);
        let scope;
        for (const name of ['tsconfig.json', 'jsconfig.json']) {
            const configPath = path.join(directory, name);
            if (!fs.existsSync(configPath)) continue;
            const known = this.aliasScopes.find(
                candidate => canonicalKey(candidate.configPath) === canonicalKey(configPath));
            try {
                scope = known ?? readAliasConfig(configPath).scope;
            } catch {
                scope = undefined;
            }
            if (scope && !known) scope = this.withOptions(scope);
            break;
        }
        this.nestedScopes.set(key, scope);
        return scope;
    }

    /**
     * The alias scopes to try for an import written in `containingFile`, as an editor
     * would choose them: `inner`, those of the configs in the directories that hold the
     * file below the root, nearest first, are tried before the root config; `outer`,
     * every other referenced config, only after it. So an alias two projects each
     * declare means, in each, the directory that project maps it to.
     */
    aliasScopesFor(containingFile) {
        if (!this.files.has(canonicalKey(containingFile))) return { inner: [], outer: this.aliasScopes };
        const directoryKey = canonicalKey(path.dirname(path.resolve(containingFile)));
        let scopes = this.directoryScopes.get(directoryKey);
        if (!scopes) {
            scopes = this.aliasScopesIn(path.dirname(path.resolve(containingFile)));
            this.directoryScopes.set(directoryKey, scopes);
        }
        return scopes;
    }

    aliasScopesIn(fileDirectory) {
        const inner = [];
        {
            const rootKey = canonicalKey(this.rootPath);
            const isBelowRoot = directory => {
                const relative = path.relative(rootKey, canonicalKey(directory));
                return relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative);
            };
            let directory = fileDirectory;
            for (let depth = 0; depth < MAX_NESTED_CONFIG_DEPTH && isBelowRoot(directory); depth++) {
                // A config a referenced project keeps beside the nearest one (a
                // tsconfig.app.json) belongs to the same directory.
                const beside = this.aliasScopes.filter(
                    candidate => canonicalKey(candidate.directory) === canonicalKey(directory));
                for (const scope of [this.nestedScope(directory), ...beside]) {
                    if (scope && !inner.includes(scope)) inner.push(scope);
                }
                directory = path.dirname(directory);
            }
        }
        return { inner, outer: this.aliasScopes.filter(scope => !inner.includes(scope)) };
    }

    /** Drops everything remembered about a file that left the workspace. */
    forget(key) {
        this.files.delete(key);
        this.embeddedSources.delete(key);
        this.moduleTargets.delete(key);
    }

    hasFile(p) {
        return this.files.has(canonicalKey(p));
    }

    getScriptSource(fileName) {
        const key = canonicalKey(fileName);
        const entry = this.files.get(key);
        const adapter = embeddedLanguageAdapters.get(path.extname(fileName).toLowerCase());
        if (!entry || !adapter) {
            try {
                const text = fs.readFileSync(fileName, 'utf8');
                return { text, scriptKind: ts.getScriptKindFromFileName(fileName), diagnostics: [] };
            } catch {
                return undefined;
            }
        }

        const cached = this.embeddedSources.get(key);
        if (cached?.version === entry.version) return cached;
        let physicalSource;
        try {
            physicalSource = fs.readFileSync(entry.path, 'utf8');
            const transformed = adapter.transform(entry.path, physicalSource);
            const result = { version: entry.version, ...transformed };
            this.embeddedSources.set(key, result);
            return result;
        } catch (error) {
            const result = {
                version: entry.version,
                text: physicalSource === undefined ? '' : sourceShapedWhitespace(physicalSource),
                scriptKind: ts.ScriptKind.TS,
                diagnostics: [{
                    filePath: entry.path,
                    severity: 'warning',
                    message: `Embedded source extraction failed: ${error.message}`,
                }],
            };
            this.embeddedSources.set(key, result);
            return result;
        }
    }

    getEmbeddedDiagnostics(fileName) {
        return this.getScriptSource(fileName)?.diagnostics || [];
    }

    /** The names an embedded file's markup uses from its script (see collectVueTemplateUsage). */
    templateReferences(fileName) {
        if (!embeddedLanguageAdapters.has(path.extname(fileName).toLowerCase())) return [];
        return this.getScriptSource(fileName)?.templateReferences || [];
    }

    /** The workspace file an import in `containingFile` resolved to, as the workspace
     * spells its path, or undefined for a package or an unresolvable name. */
    importedProjectFile(containingFile, moduleName) {
        const target = this.moduleTargets.get(canonicalKey(containingFile))?.get(moduleName);
        return target ? this.files.get(canonicalKey(target))?.path : undefined;
    }

    resolveModule(moduleName, containingFile) {
        const { inner, outer } = this.aliasScopesFor(containingFile);
        const resolveIn = scopes => {
            for (const scope of scopes) {
                // A catch-all `*` names no alias, and would send every package import here.
                if (!Object.keys(scope.paths).some(
                    pattern => pattern !== '*' && matchPathPattern(pattern, moduleName) !== undefined)) {
                    continue;
                }
                const scoped = ts.resolveModuleName(
                    moduleName, containingFile, scope.options, this.serviceHost).resolvedModule;
                if (scoped) return scoped;
            }
            return undefined;
        };

        // An alias the root config does not declare may be declared by a config it
        // references. One outside the directories that hold the importing file is
        // honoured only once the root config has failed to resolve the import.
        const resolved = resolveIn(inner)
            ?? ts.resolveModuleName(moduleName, containingFile, this.compilerOptions, this.serviceHost).resolvedModule
            ?? resolveIn(outer);
        if (resolved) return resolved;

        const scopes = [...inner, this.compilerOptions, ...outer];
        for (const candidate of this.embeddedModuleCandidates(moduleName, containingFile, scopes)) {
            if (!this.hasFile(candidate)) continue;
            const scriptKind = this.getScriptSource(candidate)?.scriptKind ?? ts.ScriptKind.TS;
            const extension = scriptKind === ts.ScriptKind.JS ? ts.Extension.Js
                : scriptKind === ts.ScriptKind.JSX ? ts.Extension.Jsx
                    : scriptKind === ts.ScriptKind.TSX ? ts.Extension.Tsx
                        : ts.Extension.Ts;
            return { resolvedFileName: path.resolve(candidate), extension, isExternalLibraryImport: false };
        }
        return undefined;
    }

    embeddedModuleCandidates(moduleName, containingFile, scopes) {
        const candidates = [];
        const addCandidate = candidate => {
            candidates.push(candidate);
            if (!path.extname(candidate)) candidates.push(candidate + '.vue');
        };
        if (moduleName.startsWith('.') || path.isAbsolute(moduleName)) {
            addCandidate(path.resolve(path.dirname(containingFile), moduleName));
            return candidates;
        }

        for (const scope of scopes) {
            // `paths` are relative to baseUrl, or else to the config that declares them,
            // which for an inherited table is the extended config, not the root one.
            const base = scope.baseUrl
                ? path.resolve(this.rootPath, scope.baseUrl)
                : scope.pathsBasePath ?? this.rootPath;
            for (const [pattern, replacements] of Object.entries(scope.paths || {})) {
                const matched = matchPathPattern(pattern, moduleName);
                if (matched === undefined) continue;
                for (const replacement of replacements) {
                    addCandidate(path.resolve(base, replacement.replace('*', matched)));
                }
            }
        }
        return candidates;
    }

    /** True when the markup of an embedded file has declared `name` itself (a `v-for`
     * or `v-slot` name) at `offset`, so that it is not the script's there. */
    isHiddenInTemplate(fileName, offset, name) {
        if (!embeddedLanguageAdapters.has(path.extname(fileName).toLowerCase())) return false;
        return (this.getScriptSource(fileName)?.templateHidden || []).some(expression =>
            offset >= expression.start && offset < expression.end && expression.hidden.has(name));
    }

    /** Forgets what the last analysis read from disk about the project, so that a
     * declaration file generated since (one the host does not watch) is read again. */
    beginAnalysis() {
        this.directoryComponents = new Map();
    }

    /** The components declared by the generated files of one directory: name to the
     * module specifier's absolute path, which may lack its extension. */
    componentsDeclaredIn(directory) {
        const key = canonicalKey(directory);
        let components = this.directoryComponents.get(key);
        if (components) return components;
        components = new Map();
        for (const relative of GLOBAL_COMPONENT_DECLARATIONS) {
            const declarationPath = path.join(directory, relative);
            let stamp;
            try {
                stamp = fs.statSync(declarationPath).mtimeMs;
            } catch {
                continue;
            }
            const cacheKey = canonicalKey(declarationPath);
            let read = this.componentDeclarations.get(cacheKey);
            if (read?.stamp !== stamp) {
                read = { stamp, components: readGlobalComponents(declarationPath) };
                this.componentDeclarations.set(cacheKey, read);
            }
            for (const [name, target] of read.components) {
                if (!components.has(name)) components.set(name, target);
            }
        }
        this.directoryComponents.set(key, components);
        return components;
    }

    /**
     * The project file of a component that no script imports but the project declares
     * for every template: the first of `names` found in a declaration file that Nuxt or
     * unplugin-vue-components generated, nearest the rendering file first.
     */
    globalComponentFile(containingFile, names) {
        const rootKey = canonicalKey(this.rootPath);
        let directory = path.dirname(path.resolve(containingFile));
        for (let depth = 0; depth < MAX_NESTED_CONFIG_DEPTH; depth++) {
            const components = this.componentsDeclaredIn(directory);
            for (const name of names) {
                const target = components.get(name);
                if (!target) continue;
                for (const candidate of [target, ...SOURCE_EXTENSIONS.flatMap(
                    extension => [target + extension, path.join(target, 'index' + extension)])]) {
                    const entry = this.files.get(canonicalKey(candidate));
                    if (entry) return entry.path;
                }
                return undefined; // declared, but outside the project
            }
            const parent = path.dirname(directory);
            if (canonicalKey(directory) === rootKey || parent === directory) break;
            directory = parent;
        }
        return undefined;
    }

    /** Re-roots the workspace (and reloads tsconfig options) if the host opens it
     * with a different root than the one initialize carried. */
    setRootPath(rootPath) {
        const resolved = path.resolve(rootPath);
        if (canonicalKey(resolved) === canonicalKey(this.rootPath)) return;
        this.rootPath = resolved;
        this.applyConfiguration(loadCompilerOptions(resolved));
    }

    filePaths() {
        return [...this.files.values()].map(f => f.path);
    }

    bump(p) {
        const key = canonicalKey(p);
        const existing = this.files.get(key);
        this.files.set(key, { path: path.resolve(p), version: (existing?.version ?? 0) + 1 });
    }

    /** Non-destructive sync against the host's approved file list. */
    syncApproved(approvedFiles) {
        const approved = new Set(approvedFiles.map(canonicalKey));
        for (const known of [...this.files.keys()]) {
            if (!approved.has(known)) this.forget(known);
        }
        for (const file of approvedFiles) {
            if (!this.files.has(canonicalKey(file))) this.bump(file);
        }
    }

    replaceFiles(fileNames) {
        const previous = this.files;
        this.files = new Map();
        for (const file of fileNames) {
            const key = canonicalKey(file);
            const existing = previous.get(key);
            this.files.set(key, { path: path.resolve(file), version: (existing?.version ?? 0) + 1 });
        }
        for (const key of previous.keys()) {
            if (!this.files.has(key)) this.forget(key);
        }
    }

    applyChanges(changes) {
        const touched = [];
        for (const change of changes) {
            touched.push(path.resolve(change.path));
            if (change.changeType === 'deleted') {
                this.forget(canonicalKey(change.path));
            } else {
                if (change.changeType === 'renamed' && change.oldPath) {
                    this.forget(canonicalKey(change.oldPath));
                    // The old path must be inside the delta's replacement scope or
                    // the renamed file's previous facts would linger in the graph.
                    touched.push(path.resolve(change.oldPath));
                }
                this.bump(change.path);
            }
        }
        return touched;
    }

    /**
     * Seeds the fact-hash map from a full (index/reset) analysis: every walked file's
     * bucket hash becomes the baseline the next incremental apply diffs against. The
     * whole-workspace path replaces the whole workspace downstream, so the previous map is
     * discarded wholesale. Mirrors CSharpWorkspaceAnalyzer.SeedFactHashes.
     */
    seedFactHashes(buckets) {
        this.factHashes = new Map();
        for (const bucket of buckets) {
            this.factHashes.set(canonicalKey(bucket.path), {
                hash: computeBucketHash(bucket),
                path: bucket.path,
            });
        }
    }

    /**
     * Diffs freshly walked buckets against the stored hash map and produces the scoped
     * incremental emission WITHOUT mutating the map. Dirty = buckets whose hash differs
     * from (or is absent in) the stored map; removed = stored keys with no current bucket
     * (deleted files, renamed old paths, files dropped by workspace/open's syncApproved,
     * unreadable files). The emission carries the dirty buckets' facts (concatenated in
     * bucket order) and replacesFiles = dirty ∪ removed as verbatim paths. Commit the
     * pending hashes only after every chunk has been accepted (see commitIncremental), so
     * a failed send leaves the map untouched and the next batch re-diffs.
     * Mirrors CSharpWorkspaceAnalyzer.BuildIncrementalEmission.
     */
    buildIncrementalEmission(buckets) {
        const nodes = [];
        const edges = [];
        const replacesFiles = [];
        const pendingHashes = new Map(); // canonicalKey -> { hash, path }
        const currentKeys = new Set();

        for (const bucket of buckets) {
            const key = canonicalKey(bucket.path);
            currentKeys.add(key);
            const hash = computeBucketHash(bucket);
            pendingHashes.set(key, { hash, path: bucket.path });
            const stored = this.factHashes.get(key);
            if (!stored || stored.hash !== hash) {
                nodes.push(...bucket.nodes);
                edges.push(...bucket.edges);
                replacesFiles.push(bucket.path);
            }
        }

        const removedKeys = [];
        for (const [key, value] of this.factHashes) {
            if (!currentKeys.has(key)) {
                removedKeys.push(key);
                replacesFiles.push(value.path);
            }
        }

        return { nodes, edges, replacesFiles, pendingHashes, removedKeys };
    }

    /**
     * Commits an emission's hash changes: stores the added/changed buckets' new hashes and
     * deletes the removed files' entries, so the map again mirrors exactly the facts the
     * host now holds. Call only after every chunk has been accepted.
     * Mirrors CSharpWorkspaceAnalyzer.CommitIncremental.
     */
    commitIncremental(emission) {
        for (const [key, value] of emission.pendingHashes) {
            this.factHashes.set(key, value);
        }
        for (const key of emission.removedKeys) {
            this.factHashes.delete(key);
        }
    }
}

/** The text `*` stands for when `moduleName` matches a tsconfig `paths` pattern ('' for
 * a pattern without one), or undefined when it does not match. */
function matchPathPattern(pattern, moduleName) {
    const star = pattern.indexOf('*');
    if (star < 0) return moduleName === pattern ? '' : undefined;
    const prefix = pattern.substring(0, star);
    const suffix = pattern.substring(star + 1);
    if (moduleName.length < prefix.length + suffix.length
        || !moduleName.startsWith(prefix) || !moduleName.endsWith(suffix)) {
        return undefined;
    }
    return moduleName.substring(prefix.length, moduleName.length - suffix.length);
}

const MAX_NESTED_CONFIG_DEPTH = 64;
const MAX_REFERENCED_CONFIGS = 32;

// Reads a config for its compilerOptions alone. The host approves the file set, so a
// config's `include` is never expanded and reading one never walks a directory.
const OPTIONS_ONLY_CONFIG_HOST = {
    useCaseSensitiveFileNames: ts.sys.useCaseSensitiveFileNames,
    fileExists: ts.sys.fileExists,
    readFile: ts.sys.readFile,
    readDirectory: () => [],
};

// Where a project declares the components every template may render without importing
// them, relative to the project's directory: Nuxt writes the first two (which of them
// holds the interface depends on its version), unplugin-vue-components the third.
const NUXT_COMPONENT_DECLARATIONS = [
    path.join('.nuxt', 'types', 'components.d.ts'), path.join('.nuxt', 'components.d.ts'),
];
const GLOBAL_COMPONENT_DECLARATIONS = [...NUXT_COMPONENT_DECLARATIONS, 'components.d.ts'];

/**
 * Reads a generated declaration file of global components: every member of an interface
 * named `GlobalComponents` (or `_GlobalComponents`) whose type imports a module by a
 * relative path, as `'BaseButton': typeof import("../components/base/Button.vue")['default']`,
 * and every exported constant so typed, which is how some Nuxt versions write the same.
 * Returns name to that module's absolute path. Nothing in the file is executed.
 */
function readGlobalComponents(declarationPath) {
    const components = new Map();
    let text;
    try {
        text = fs.readFileSync(declarationPath, 'utf8');
    } catch {
        return components;
    }
    const directory = path.dirname(declarationPath);
    const source = ts.createSourceFile(declarationPath, text, ts.ScriptTarget.Latest, false, ts.ScriptKind.TS);
    const importedModule = type => {
        let found;
        const find = node => {
            if (found !== undefined) return;
            if (ts.isImportTypeNode(node) && ts.isLiteralTypeNode(node.argument)
                && ts.isStringLiteralLike(node.argument.literal)) {
                found = node.argument.literal.text;
                return;
            }
            ts.forEachChild(node, find);
        };
        find(type);
        return found;
    };
    const declare = (name, type) => {
        if (!type || !name || !(ts.isIdentifier(name) || ts.isStringLiteralLike(name))) return;
        const moduleName = importedModule(type);
        if (moduleName && (moduleName.startsWith('.') || path.isAbsolute(moduleName))
            && !components.has(name.text)) {
            components.set(name.text, path.resolve(directory, moduleName));
        }
    };
    const visit = node => {
        if (ts.isInterfaceDeclaration(node) && /^_?GlobalComponents$/.test(node.name.text)) {
            for (const member of node.members) {
                if (ts.isPropertySignature(member)) declare(member.name, member.type);
            }
        }
        ts.forEachChild(node, visit);
    };
    visit(source);
    for (const statement of source.statements) {
        if (ts.isVariableStatement(statement)
            && statement.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword)) {
            for (const declaration of statement.declarationList.declarations) {
                declare(declaration.name, declaration.type);
            }
        }
    }
    return components;
}

/**
 * Collects the `paths` tables of the given configs and of every config they reference.
 * A solution-style root config (`"files": []` plus `references`) declares no aliases of
 * its own; the projects it references do.
 */
function loadAliasScopes(configPaths) {
    const scopes = [];
    const visited = new Set();
    const pending = [...configPaths];
    while (pending.length > 0 && visited.size < MAX_REFERENCED_CONFIGS) {
        let configPath = pending.shift();
        try {
            if (fs.statSync(configPath).isDirectory()) configPath = path.join(configPath, 'tsconfig.json');
            if (visited.has(canonicalKey(configPath))) continue;
            visited.add(canonicalKey(configPath));
            const read = readAliasConfig(configPath);
            if (read.scope) scopes.push(read.scope);
            pending.push(...read.references);
        } catch {
            // A reference to a missing or unreadable config declares no aliases.
        }
    }
    return scopes;
}

/** The alias scope one config declares (undefined when it has no `paths`, its own or
 * inherited) and the configs it references. Throws when the file cannot be read. */
function readAliasConfig(configPath) {
    const read = ts.readConfigFile(configPath, ts.sys.readFile);
    if (read.error) return { scope: undefined, references: [] };
    const directory = path.dirname(configPath);
    const parsed = ts.parseJsonConfigFileContent(read.config, OPTIONS_ONLY_CONFIG_HOST, directory);
    const scope = parsed.options.paths
        ? {
            configPath,
            directory,
            paths: parsed.options.paths,
            baseUrl: parsed.options.baseUrl,
            pathsBasePath: parsed.options.pathsBasePath ?? directory,
        }
        : undefined;
    return { scope, references: (parsed.projectReferences ?? []).map(reference => reference.path) };
}

function loadCompilerOptions(rootPath) {
    const defaults = {
        allowJs: true,
        allowNonTsExtensions: true,
        allowArbitraryExtensions: true,
        checkJs: false,
        target: ts.ScriptTarget.Latest,
        module: ts.ModuleKind.ESNext,
        moduleResolution: ts.ModuleResolutionKind.Bundler,
        jsx: ts.JsxEmit.Preserve,
        skipLibCheck: true,
        noEmit: true,
    };
    let configPath = path.join(rootPath, 'tsconfig.json');
    const fallbackDiagnostic = message => ({
        options: defaults,
        aliasScopes: [],
        diagnostic: {
            filePath: configPath,
            severity: 'warning',
            message: `${path.basename(configPath)} could not be used (${message}); indexing with permissive defaults.`,
        },
    });
    try {
        // A JavaScript project declares its options and aliases in jsconfig.json.
        const jsConfigPath = path.join(rootPath, 'jsconfig.json');
        if (!fs.existsSync(configPath) && fs.existsSync(jsConfigPath)) configPath = jsConfigPath;
        let options = defaults;
        // Nuxt builds with aliases (`~`, `@`) that only the config it generates declares,
        // and a project need not extend or reference that config, or have one at all.
        const aliasConfigs = [path.join(rootPath, '.nuxt', 'tsconfig.json')];
        if (fs.existsSync(configPath)) {
            const read = ts.readConfigFile(configPath, ts.sys.readFile);
            if (read.error) {
                return fallbackDiagnostic(ts.flattenDiagnosticMessageText(read.error.messageText, ' '));
            }
            const parsed = ts.parseJsonConfigFileContent(read.config, OPTIONS_ONLY_CONFIG_HOST, rootPath);
            options = { ...defaults, ...parsed.options, noEmit: true };
            aliasConfigs.unshift(...(parsed.projectReferences ?? []).map(reference => reference.path));
        }
        return { options, aliasScopes: loadAliasScopes(aliasConfigs), diagnostic: undefined };
    } catch (error) {
        return fallbackDiagnostic(error.message);
    }
}

// ---------------------------------------------------------------------------
// Analysis: AST walk + type-checker resolution into normalized nodes/edges
// ---------------------------------------------------------------------------

function relPath(rootPath, fileName) {
    const rel = path.relative(rootPath, fileName);
    const portable = (rel && !rel.startsWith('..') ? rel : fileName).split(path.sep).join('/');
    return portable;
}

function getVisibility(node) {
    const flags = ts.getCombinedModifierFlags(node);
    if (flags & ts.ModifierFlags.Private) return 'private';
    if (flags & ts.ModifierFlags.Protected) return 'protected';
    return 'public';
}

function signatureOf(node, sourceFile) {
    const start = node.getStart(sourceFile);
    // Use the parser's structural body/open-brace boundary. Searching raw text for
    // the first "{" truncates valid signatures containing object/type literals.
    let boundary = node.body && typeof node.body.getStart === 'function'
        ? node.body.getStart(sourceFile)
        : undefined;
    if (boundary === undefined) {
        const openBrace = node.getChildren(sourceFile)
            .find(child => child.kind === ts.SyntaxKind.OpenBraceToken);
        boundary = openBrace?.getStart(sourceFile);
    }
    const text = sourceFile.text.substring(start, boundary ?? node.getEnd());
    return text.split('\n', 1)[0].trim();
}

/** True for a FunctionDeclaration/ClassDeclaration carrying an `export default` modifier. */
function hasDefaultModifier(node) {
    const modifiers = ts.canHaveModifiers(node) ? ts.getModifiers(node) : undefined;
    return !!modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.DefaultKeyword);
}

/**
 * True for the arrow function directly assigned by `export default () => {...}`.
 * `export = () => {...}` (isExportEquals — TS's legacy CommonJS-style export) has
 * the identical ExportAssignment/arrow shape but is deliberately NOT emitted as a
 * node below, so it must be excluded here too or this returns true for an arrow
 * with no emitted node — the same dangling-edge-source bug this fix closes.
 */
function isDefaultExportedArrow(node) {
    return ts.isArrowFunction(node) && node.parent && ts.isExportAssignment(node.parent)
        && !node.parent.isExportEquals && node.parent.expression === node;
}

/**
 * Builds the container-qualified name of a declaration inside its file
 * (namespaces + class/interface chain + own name), used for stable IDs. An
 * anonymous `export default function(){}` / `class {}` / `() => {}` has no
 * name of its own; it's given the synthetic segment "default" so that a node
 * IS emitted for it and any edge walking up to it (e.g. enclosingDeclarationId)
 * lands on that same id instead of an empty-string id nothing ever emits.
 */
function qualifiedNameOf(node, sourceFile) {
    const parts = [];
    let current = node;
    while (current && current !== sourceFile) {
        if ((ts.isClassDeclaration(current) || ts.isInterfaceDeclaration(current)
            || ts.isModuleDeclaration(current) || ts.isEnumDeclaration(current)
            || ts.isTypeAliasDeclaration(current))
            && current.name) {
            parts.unshift(current.name.getText(sourceFile));
        } else if (ts.isClassDeclaration(current) && !current.name && hasDefaultModifier(current)) {
            parts.unshift('default');
        } else if (ts.isFunctionDeclaration(current) && current.name) {
            parts.unshift(`${current.name.getText(sourceFile)}${parameterKey(current, sourceFile)}`);
        } else if (ts.isFunctionDeclaration(current) && !current.name && hasDefaultModifier(current)) {
            parts.unshift(`default${parameterKey(current, sourceFile)}`);
        } else if (isDefaultExportedArrow(current)) {
            parts.unshift('default');
        } else if (ts.isConstructorDeclaration(current)) {
            parts.unshift(`constructor${parameterKey(current, sourceFile)}`);
        } else if ((ts.isMethodDeclaration(current) || ts.isMethodSignature(current)) && current.name) {
            parts.unshift(`${current.name.getText(sourceFile)}${parameterKey(current, sourceFile)}`);
        } else if ((ts.isPropertyDeclaration(current) || ts.isPropertySignature(current)) && current.name) {
            parts.unshift(current.name.getText(sourceFile));
        } else if ((ts.isGetAccessor(current) || ts.isSetAccessor(current)) && current.name) {
            const accessor = ts.isGetAccessor(current) ? 'get' : 'set';
            parts.unshift(`${accessor}:${current.name.getText(sourceFile)}`);
        } else if (ts.isVariableDeclaration(current) && current.name) {
            parts.unshift(current.name.getText(sourceFile));
        }
        current = current.parent;
    }
    return parts.join('.');
}

/**
 * The arrow function actually held by a declaration's initializer: itself, or
 * — for a handler wrapped by a single-level HOC called by a bare name, like
 * `guard(fn)` / `withAuth(fn)` — the arrow function passed as its last
 * argument. Without this unwrap, a route handler such as `const revoke =
 * guard(async () => {...})` has an initializer of kind CallExpression, not
 * ArrowFunction, so calls nested inside it (and the declaration's own node)
 * fall through to file scope instead of attributing to `revoke`.
 *
 * The callee is required to be a bare identifier specifically to exclude
 * method calls like `.map(fn)` / `.filter(fn)` / `.then(fn)` — their callee is
 * a PropertyAccessExpression, and their trailing-arrow result is a value
 * (array/promise), never itself callable. Without this guard, `const ids =
 * users.map(u => lookup(u.id))` would wrongly mint a Function node for `ids`
 * and attribute `lookup`'s CALLS edge to it.
 */
function arrowFunctionInitializer(initializer) {
    if (!initializer) return undefined;
    if (ts.isArrowFunction(initializer)) return initializer;
    if (ts.isCallExpression(initializer) && ts.isIdentifier(initializer.expression)
        && initializer.arguments.length > 0) {
        const lastArg = initializer.arguments[initializer.arguments.length - 1];
        if (ts.isArrowFunction(lastArg)) return lastArg;
    }
    return undefined;
}

/** Stable overload discriminator without relying on source offsets. */
function parameterKey(node, sourceFile) {
    const parameters = node.parameters || [];
    return `(${parameters.map(parameter => {
        const rest = parameter.dotDotDotToken ? '...' : '';
        const optional = parameter.questionToken || parameter.initializer ? '?' : '';
        const type = parameter.type
            ? parameter.type.getText(sourceFile)
            : parameter.name.getText(sourceFile);
        return `${rest}${type}${optional}`;
    }).join(',')})`;
}

function makeEdge(sourceId, targetId, kind, metadata) {
    // Line AND column in the ID: two same-target calls on one line must not
    // silently collapse into a single edge.
    const suffix = metadata && metadata.line
        ? `@${metadata.line}:${metadata.column ?? '0'}`
        : '';
    return {
        id: `${sourceId}=[${kind}]=>${targetId}${suffix}`,
        sourceId,
        targetId,
        kind,
        metadata: metadata && Object.keys(metadata).length > 0 ? metadata : undefined,
    };
}

function analyzeFile(workspace, workspaceId, program, checker, fileName, diagnostics) {
    const sourceFile = program.getSourceFile(fileName);
    const nodes = [];
    const edges = [];
    if (!sourceFile) {
        // Distinguish "file could not be read" from "no declarations": an
        // unreadable file must not silently index as empty.
        diagnostics.push({
            filePath: fileName,
            severity: 'warning',
            message: 'File could not be read or loaded into the program; its facts were replaced with nothing.',
        });
        return { nodes, edges };
    }

    const root = workspace.rootPath;
    const fileRel = relPath(root, fileName);
    const graphPrefix = `typescript:${encodeURIComponent(workspaceId)}:`;
    const publicIdentifier = qualified => `typescript:${fileRel}#${qualified}`;

    const nodeId = qualified => `${graphPrefix}${fileRel}#${qualified}`;

    const idOfDeclaration = decl => {
        const declFile = decl.getSourceFile();
        return `${graphPrefix}${relPath(root, declFile.fileName)}#${qualifiedNameOf(decl, declFile)}`;
    };

    /**
     * Resolves a name expression to a declaration inside the project, if any.
     * When `callLikeExpression` (the enclosing CallExpression/NewExpression) is
     * given, an overloaded target first tries the checker's own resolved
     * signature for that specific call site — otherwise an overloaded function
     * always resolves to declarations[0], i.e. whichever overload was declared
     * first in source order, regardless of which one the arguments actually
     * matched.
     */
    const resolveToProjectDeclaration = (expression, callLikeExpression) => {
        try {
            if (callLikeExpression) {
                const signatureDecl = checker.getResolvedSignature(callLikeExpression)?.declaration;
                // Restricted to declaration kinds this file always emits a node for
                // (regardless of overload count), so this can never target a kind —
                // e.g. a bare CallSignatureDeclaration — that addNode never visits.
                if (signatureDecl
                    && (ts.isFunctionDeclaration(signatureDecl) || ts.isMethodDeclaration(signatureDecl)
                        || ts.isMethodSignature(signatureDecl) || ts.isConstructorDeclaration(signatureDecl))
                    && workspace.hasFile(signatureDecl.getSourceFile().fileName)) {
                    return signatureDecl;
                }
            }
            let symbol = checker.getSymbolAtLocation(expression);
            if (!symbol) return null;
            if (symbol.flags & ts.SymbolFlags.Alias) {
                symbol = checker.getAliasedSymbol(symbol);
            }
            const decl = symbol.declarations && symbol.declarations[0];
            if (!decl) return null;
            if (!workspace.hasFile(decl.getSourceFile().fileName)) return null;
            return decl;
        } catch {
            return null;
        }
    };

    const moduleIdOf = file => `${graphPrefix}${relPath(root, file)}`;

    /** The declaration `addNode` emits a node for when `decl` declares a function,
     * class, or arrow-function variable; null for anything else. */
    const emittedDeclaration = decl => {
        if (ts.isExportAssignment(decl) && isDefaultExportedArrow(decl.expression)) return decl.expression;
        if ((ts.isFunctionDeclaration(decl) || ts.isClassDeclaration(decl))
            && (decl.name || hasDefaultModifier(decl))) {
            return decl;
        }
        if (ts.isVariableDeclaration(decl) && arrowFunctionInitializer(decl.initializer)
            && decl.parent?.parent && ts.isVariableStatement(decl.parent.parent)) {
            return decl;
        }
        return null;
    };

    /** The node a rendered component is: its declaration when one is emitted, else the
     * other module that declares it (`export default {...}`, `const X =
     * defineComponent(...)`). A name this file binds to anything else (a parameter, a
     * ref, a value picked at run time) names no component here, so null. */
    const componentTargetId = decl => {
        const emitted = emittedDeclaration(decl);
        if (emitted) return idOfDeclaration(emitted);
        const declFile = decl.getSourceFile();
        return declFile === sourceFile ? null : moduleIdOf(declFile.fileName);
    };

    /**
     * Binds a name used by the markup of an embedded file to a project node. A component
     * imported from a file with no default export the compiler can see (a Vue SFC
     * without `export default`) is that file's module.
     */
    const templateTargetId = (symbol, isComponent) => {
        const target = symbol.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(symbol) : symbol;
        const decl = target.declarations && target.declarations[0];
        if (decl) {
            if (!workspace.hasFile(decl.getSourceFile().fileName)) return null;
            if (isComponent) return componentTargetId(decl);
            const emitted = emittedDeclaration(decl);
            return emitted ? idOfDeclaration(emitted) : null;
        }
        if (!isComponent) return null;
        let importNode = symbol.declarations && symbol.declarations[0];
        while (importNode && !ts.isImportDeclaration(importNode)) importNode = importNode.parent;
        if (!importNode || !ts.isStringLiteralLike(importNode.moduleSpecifier)) return null;
        const file = workspace.importedProjectFile(fileName, importNode.moduleSpecifier.text);
        return file ? moduleIdOf(file) : null;
    };

    /**
     * The node of the function a name used as a value names: a function, a method of a
     * class, or a variable or field holding an arrow function. Null for any other value,
     * and for a function this project does not declare.
     */
    const functionValueTargetId = symbol => {
        if (!symbol) return null;
        const target = symbol.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(symbol) : symbol;
        const decl = target.declarations && target.declarations[0];
        if (!decl || ts.isClassDeclaration(decl) || !workspace.hasFile(decl.getSourceFile().fileName)) return null;
        const emitted = emittedDeclaration(decl);
        if (emitted) return idOfDeclaration(emitted);
        const isMember = (ts.isMethodDeclaration(decl) && ts.isClassLike(decl.parent))
            || (ts.isPropertyDeclaration(decl) && arrowFunctionInitializer(decl.initializer));
        return isMember ? idOfDeclaration(decl) : null;
    };

    const namespaceStack = [];
    const currentNamespace = () => namespaceStack.join('.');

    const addNode = (name, kind, tsNode, extra = {}) => {
        const start = sourceFile.getLineAndCharacterOfPosition(tsNode.getStart(sourceFile));
        const end = sourceFile.getLineAndCharacterOfPosition(tsNode.getEnd());
        const node = {
            id: nodeId(qualifiedNameOf(tsNode, sourceFile) || name),
            identifier: publicIdentifier(qualifiedNameOf(tsNode, sourceFile) || name),
            name,
            kind,
            language: 'typescript',
            filePath: fileName,
            startLine: start.line + 1,
            endLine: end.line + 1,
            startColumn: start.character + 1,
            endColumn: end.character + 1,
            namespace: currentNamespace(),
            visibility: getVisibility(tsNode),
            signature: signatureOf(tsNode, sourceFile),
            ...extra,
        };
        nodes.push(node);
        return node;
    };

    // Calls and imports at executable file scope need a real graph source. Previous
    // releases emitted edges from this ID without emitting the node, so repository
    // traversal discarded otherwise correctly resolved top-level invocations.
    nodes.push({
        id: `${graphPrefix}${fileRel}`,
        identifier: `typescript:${fileRel}`,
        name: fileRel,
        kind: 'Module',
        language: 'typescript',
        filePath: fileName,
        startLine: 1,
        endLine: sourceFile.getLineAndCharacterOfPosition(sourceFile.getEnd()).line + 1,
        startColumn: 1,
        endColumn: 1,
        namespace: '',
        visibility: 'internal',
        signature: fileRel,
        metadata: { moduleScope: 'true' },
    });

    const addEdge = (sourceId, targetId, kind, metadata) => {
        edges.push(makeEdge(sourceId, targetId, kind, metadata));
    };

    // Components this file registers for every template (`app.component('Name', X)`),
    // and component tags of its own markup that nothing it can see declares: the
    // workspace matches one against the other once every file has been read.
    const componentRegistrations = [];
    const unboundComponentTags = [];

    /** Nearest enclosing emitted declaration to use as a CALLS source. */
    const enclosingDeclarationId = tsNode => {
        let current = tsNode.parent;
        while (current && current !== sourceFile) {
            if (ts.isMethodDeclaration(current) || ts.isConstructorDeclaration(current)
                || ts.isFunctionDeclaration(current) || ts.isGetAccessor(current)
                || ts.isSetAccessor(current)) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            if (ts.isVariableDeclaration(current) && arrowFunctionInitializer(current.initializer)) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            if (ts.isPropertyDeclaration(current) && arrowFunctionInitializer(current.initializer)) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            if (isDefaultExportedArrow(current)) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            // Falls back to the class itself for anything in its body with no closer
            // match above: static blocks (`static { init(); }`), decorator factories
            // (`@Component({...})`), and non-arrow field initializers. A nearer
            // MethodDeclaration/PropertyDeclaration-arrow always wins first, since the
            // walk checks the closest ancestor before this one.
            if (ts.isClassDeclaration(current) && (current.name || hasDefaultModifier(current))) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            current = current.parent;
        }
        return `${graphPrefix}${fileRel}`;
    };

    const handleHeritage = (declared, declNode) => {
        for (const heritage of declNode.heritageClauses ?? []) {
            const edgeKind = heritage.token === ts.SyntaxKind.ExtendsKeyword ? 'EXTENDS' : 'IMPLEMENTS';
            for (const typeRef of heritage.types) {
                const typeName = typeRef.expression.getText(sourceFile);
                const targetDecl = resolveToProjectDeclaration(typeRef.expression);
                const targetId = targetDecl ? idOfDeclaration(targetDecl) : typeName;
                addEdge(declared.id, targetId, edgeKind,
                    targetDecl ? { targetName: typeName } : { targetName: typeName, unresolved: 'true' });

                if (!targetDecl) continue;
                const memberEdgeKind = heritage.token === ts.SyntaxKind.ExtendsKeyword
                    ? 'OVERRIDES_MEMBER'
                    : 'IMPLEMENTS_MEMBER';
                const targetType = checker.getTypeAtLocation(typeRef);
                for (const member of declNode.members ?? []) {
                    if ((!ts.isMethodDeclaration(member) && !ts.isMethodSignature(member)) || !member.name) continue;
                    const memberName = member.name.getText(sourceFile);
                    const inherited = targetType.getProperty(memberName);
                    const candidates = (inherited?.declarations ?? []).filter(inheritedDecl =>
                        (ts.isMethodDeclaration(inheritedDecl) || ts.isMethodSignature(inheritedDecl))
                        && workspace.hasFile(inheritedDecl.getSourceFile().fileName));
                    const exactOverloads = candidates.filter(inheritedDecl =>
                        parameterKey(inheritedDecl, inheritedDecl.getSourceFile())
                        === parameterKey(member, sourceFile));
                    // An instantiated generic member can have a different source-text
                    // parameter key (T versus number). A single candidate is still
                    // unambiguous; overloaded families require an exact key.
                    const matched = exactOverloads.length > 0
                        ? exactOverloads
                        : candidates.length === 1 ? candidates : [];
                    for (const inheritedDecl of matched) {
                        addEdge(nodeId(qualifiedNameOf(member, sourceFile)), idOfDeclaration(inheritedDecl), memberEdgeKind,
                            { targetName: memberName });
                    }
                }
            }
        }
    };

    /**
     * `items.map(format)`, `onClick={save}`, `{ run: save }`: a function handed over
     * rather than called is a REFERENCES edge from where it is handed over. `symbol` is
     * given for a shorthand property, whose name is not itself an expression.
     */
    const addValueReference = (expression, symbol) => {
        if (!expression) return;
        const nameNode = ts.isPropertyAccessExpression(expression) ? expression.name : expression;
        if (!ts.isIdentifier(nameNode)) return;
        let base = expression;
        while (ts.isPropertyAccessExpression(base)) base = base.expression;
        if (ts.isIdentifier(base)
            && workspace.isHiddenInTemplate(fileName, base.getStart(sourceFile), base.text)) {
            return;
        }
        let targetId;
        try {
            targetId = functionValueTargetId(symbol ?? checker.getSymbolAtLocation(nameNode));
        } catch {
            return;
        }
        if (!targetId) return;
        const position = sourceFile.getLineAndCharacterOfPosition(expression.getStart(sourceFile));
        addEdge(enclosingDeclarationId(expression), targetId, 'REFERENCES', {
            line: String(position.line + 1),
            column: String(position.character + 1),
        });
    };

    const visit = tsNode => {
        switch (tsNode.kind) {
            case ts.SyntaxKind.ModuleDeclaration: {
                if (tsNode.name) {
                    const moduleName = tsNode.name.getText(sourceFile);
                    addNode(moduleName, 'Namespace', tsNode);
                    namespaceStack.push(moduleName);
                    ts.forEachChild(tsNode, visit);
                    namespaceStack.pop();
                    return;
                }
                break;
            }
            case ts.SyntaxKind.ClassDeclaration: {
                if (tsNode.name) {
                    const classNode = addNode(tsNode.name.getText(sourceFile), 'Class', tsNode);
                    handleHeritage(classNode, tsNode);
                } else if (hasDefaultModifier(tsNode)) {
                    const classNode = addNode('default', 'Class', tsNode);
                    handleHeritage(classNode, tsNode);
                }
                break;
            }
            case ts.SyntaxKind.InterfaceDeclaration: {
                if (tsNode.name) {
                    const interfaceNode = addNode(tsNode.name.getText(sourceFile), 'Interface', tsNode);
                    handleHeritage(interfaceNode, tsNode);
                }
                break;
            }
            case ts.SyntaxKind.FunctionDeclaration: {
                if (tsNode.name) {
                    addNode(tsNode.name.getText(sourceFile), 'Function', tsNode);
                } else if (hasDefaultModifier(tsNode)) {
                    addNode('default', 'Function', tsNode);
                }
                break;
            }
            case ts.SyntaxKind.ExportAssignment: {
                // `export default () => {...}` — `export = foo` (isExportEquals) is a
                // different (CommonJS-style) construct and is left alone.
                if (!tsNode.isExportEquals && tsNode.expression && ts.isArrowFunction(tsNode.expression)) {
                    addNode('default', 'Function', tsNode.expression);
                }
                break;
            }
            case ts.SyntaxKind.VariableStatement: {
                for (const declaration of tsNode.declarationList?.declarations ?? []) {
                    if (declaration.name && arrowFunctionInitializer(declaration.initializer)) {
                        addNode(declaration.name.getText(sourceFile), 'Function', declaration);
                    }
                }
                break;
            }
            case ts.SyntaxKind.MethodDeclaration:
            case ts.SyntaxKind.MethodSignature: {
                if (tsNode.name) {
                    const methodNode = addNode(tsNode.name.getText(sourceFile), 'Method', tsNode);
                    const container = tsNode.parent;
                    if (container && ((ts.isClassDeclaration(container) && (container.name || hasDefaultModifier(container)))
                        || (ts.isInterfaceDeclaration(container) && container.name))) {
                        addEdge(nodeId(qualifiedNameOf(container, sourceFile)), methodNode.id, 'HAS_METHOD');
                    }
                }
                break;
            }
            case ts.SyntaxKind.Constructor: {
                const ctorNode = addNode('constructor', 'Method', tsNode);
                const container = tsNode.parent;
                if (container && ts.isClassDeclaration(container) && (container.name || hasDefaultModifier(container))) {
                    addEdge(nodeId(qualifiedNameOf(container, sourceFile)), ctorNode.id, 'HAS_METHOD');
                }
                break;
            }
            case ts.SyntaxKind.PropertyDeclaration: {
                if (tsNode.name) {
                    const propertyNode = addNode(tsNode.name.getText(sourceFile), 'Field', tsNode);
                    const container = tsNode.parent;
                    if (container && ts.isClassDeclaration(container) && (container.name || hasDefaultModifier(container))) {
                        addEdge(nodeId(qualifiedNameOf(container, sourceFile)), propertyNode.id, 'HAS_FIELD');
                    }
                }
                break;
            }
            case ts.SyntaxKind.GetAccessor:
            case ts.SyntaxKind.SetAccessor: {
                if (tsNode.name) {
                    const propertyNode = addNode(tsNode.name.getText(sourceFile), 'Property', tsNode);
                    const container = tsNode.parent;
                    if (container && ts.isClassDeclaration(container) && (container.name || hasDefaultModifier(container))) {
                        addEdge(nodeId(qualifiedNameOf(container, sourceFile)), propertyNode.id, 'HAS_PROPERTY');
                    }
                }
                break;
            }
            case ts.SyntaxKind.PropertySignature: {
                if (tsNode.name) {
                    const propertyNode = addNode(tsNode.name.getText(sourceFile), 'Property', tsNode);
                    const container = tsNode.parent;
                    if (container && ts.isInterfaceDeclaration(container) && container.name) {
                        addEdge(nodeId(qualifiedNameOf(container, sourceFile)), propertyNode.id, 'HAS_PROPERTY');
                    }
                }
                break;
            }
            case ts.SyntaxKind.TypeAliasDeclaration: {
                if (tsNode.name) addNode(tsNode.name.getText(sourceFile), 'Type', tsNode);
                break;
            }
            case ts.SyntaxKind.EnumDeclaration: {
                if (tsNode.name) addNode(tsNode.name.getText(sourceFile), 'Enum', tsNode);
                break;
            }
            case ts.SyntaxKind.ImportDeclaration:
            case ts.SyntaxKind.ExportDeclaration: {
                // Re-exports (`export { x } from './y'`, `export * from './y'`) are a file
                // dependency exactly like an import — without this, barrel/index files that
                // only re-export look dependency-free.
                if (tsNode.moduleSpecifier) {
                    const moduleName = tsNode.moduleSpecifier.getText(sourceFile).replace(/['"]/g, '');
                    // A file with no import or export of its own (a template-only Vue
                    // component, a side-effect script) has no module symbol to bind,
                    // but the import still resolved to it.
                    const resolved = resolveToProjectDeclaration(tsNode.moduleSpecifier)?.getSourceFile().fileName
                        ?? workspace.importedProjectFile(fileName, moduleName);
                    const targetId = resolved ? moduleIdOf(resolved) : moduleName;
                    const metadata = { moduleSpecifier: moduleName, isFileImport: 'true' };
                    if (tsNode.kind === ts.SyntaxKind.ExportDeclaration) metadata.isReExport = 'true';
                    if (!resolved) metadata.unresolved = 'true'; // external package or unresolvable path
                    addEdge(`${graphPrefix}${fileRel}`, targetId, 'IMPORTS', metadata);
                }
                break;
            }
            case ts.SyntaxKind.CallExpression:
            case ts.SyntaxKind.NewExpression: {
                // `new Foo()` reuses the CALLS edge kind (construction is a call to the
                // constructor) rather than minting an unregistered edge kind no consumer
                // filters/queries recognize yet.
                const expression = tsNode.expression;
                if (expression) {
                    const nameNode = ts.isPropertyAccessExpression(expression) ? expression.name : expression;
                    const callTarget = nameNode.getText(sourceFile);
                    if (callTarget) {
                        const position = sourceFile.getLineAndCharacterOfPosition(tsNode.getStart(sourceFile));
                        const targetDecl = resolveToProjectDeclaration(nameNode, tsNode);
                        const targetId = targetDecl ? idOfDeclaration(targetDecl) : callTarget;
                        const metadata = {
                            callTarget,
                            line: String(position.line + 1),
                            column: String(position.character + 1),
                        };
                        if (!targetDecl) metadata.unresolved = 'true';
                        addEdge(enclosingDeclarationId(tsNode), targetId, 'CALLS', metadata);
                    }
                }
                for (const argument of tsNode.arguments ?? []) addValueReference(argument);
                // `app.component('BaseButton', BaseButton)` declares the component for
                // every template of the application.
                if (expression && ts.isPropertyAccessExpression(expression)
                    && expression.name.text === 'component' && tsNode.arguments?.length === 2
                    && ts.isStringLiteralLike(tsNode.arguments[0]) && ts.isIdentifier(tsNode.arguments[1])) {
                    let registered;
                    try {
                        registered = checker.getSymbolAtLocation(tsNode.arguments[1]);
                    } catch {
                        registered = undefined;
                    }
                    const targetId = registered ? templateTargetId(registered, true) : null;
                    if (targetId) componentRegistrations.push({ name: tsNode.arguments[0].text, targetId });
                }
                break;
            }
            case ts.SyntaxKind.JsxAttribute:
                if (tsNode.initializer && ts.isJsxExpression(tsNode.initializer)) {
                    addValueReference(tsNode.initializer.expression);
                }
                break;
            case ts.SyntaxKind.PropertyAssignment:
                addValueReference(tsNode.initializer);
                break;
            case ts.SyntaxKind.ShorthandPropertyAssignment:
                addValueReference(tsNode.name, checker.getShorthandAssignmentValueSymbol(tsNode));
                break;
            case ts.SyntaxKind.ArrayLiteralExpression:
                for (const element of tsNode.elements) addValueReference(element);
                break;
            case ts.SyntaxKind.JsxOpeningElement:
            case ts.SyntaxKind.JsxSelfClosingElement: {
                // `<Foo />` renders the component Foo: a CALLS edge, as for `new Foo()`.
                // A lower-case tag is an intrinsic element, not a symbol.
                const tagName = tsNode.tagName;
                const nameNode = ts.isPropertyAccessExpression(tagName) ? tagName.name : tagName;
                if (!ts.isIdentifier(nameNode)
                    || (nameNode === tagName && !/^[A-Z_$]/.test(nameNode.text))) {
                    break;
                }
                const targetDecl = resolveToProjectDeclaration(nameNode);
                const targetId = targetDecl && componentTargetId(targetDecl);
                if (targetId) {
                    const position = sourceFile.getLineAndCharacterOfPosition(tsNode.getStart(sourceFile));
                    addEdge(enclosingDeclarationId(tsNode), targetId, 'CALLS', {
                        callTarget: tagName.getText(sourceFile),
                        line: String(position.line + 1),
                        column: String(position.character + 1),
                    });
                }
                break;
            }
        }
        ts.forEachChild(tsNode, visit);
    };

    visit(sourceFile);

    // Component tags and named event handlers in an embedded file's markup: each is a
    // use, by the file's module, of whatever that name is bound to in its script.
    const templateReferences = workspace.templateReferences(fileName);
    if (templateReferences.length > 0) {
        const meaning = ts.SymbolFlags.Value | ts.SymbolFlags.Alias;
        let scope;
        const lookup = name => {
            // One lookup per name, where the compiler offers it, instead of a table of
            // every global for every file.
            if (typeof checker.resolveName === 'function') {
                return checker.resolveName(name, sourceFile.endOfFileToken, meaning, false);
            }
            if (!scope) {
                scope = new Map();
                for (const symbol of checker.getSymbolsInScope(sourceFile.endOfFileToken, meaning)) {
                    if (!scope.has(symbol.name)) scope.set(symbol.name, symbol);
                }
            }
            return scope.get(name);
        };
        for (const reference of templateReferences) {
            const symbol = reference.names.map(lookup).find(Boolean);
            let targetId = symbol ? templateTargetId(symbol, reference.component) : null;
            const position = sourceFile.getLineAndCharacterOfPosition(reference.offset);
            const metadata = {
                callTarget: reference.display,
                line: String(position.line + 1),
                column: String(position.character + 1),
            };
            // A name the script binds is the script's, whatever it is bound to. Any other
            // component is one the project declares for every template, if it does.
            const boundByScript = symbol?.declarations?.some(decl => decl.getSourceFile() === sourceFile);
            if (!targetId && reference.component && !boundByScript) {
                const declared = workspace.globalComponentFile(fileName, reference.names);
                if (declared) {
                    targetId = moduleIdOf(declared);
                } else {
                    unboundComponentTags.push({ names: reference.names, metadata });
                }
            }
            if (targetId) addEdge(`${graphPrefix}${fileRel}`, targetId, 'CALLS', metadata);
        }
    }

    return { nodes, edges, componentRegistrations, unboundComponentTags };
}

// ---------------------------------------------------------------------------
// Delta publication
// ---------------------------------------------------------------------------

/**
 * Maps each fact id to the ordinally smallest bucket path that emits it. Cross-file
 * duplicate ids are impossible in this worker (node ids and edge source ids both embed the
 * WALKED file's fileRel, so two files never mint the same id), but the pass is kept as a
 * defensive mirror of the C# analyzer and to make within-bucket dedup share one code path.
 * The winner is a pure function of the file SET (never edit order), so incremental
 * emission of only the dirty buckets can never diverge from a fresh whole-tree scan.
 */
function computeOwners(buckets, factsOf, idOf) {
    const owner = new Map();
    for (const bucket of buckets) {
        for (const fact of factsOf(bucket)) {
            const key = idOf(fact);
            const current = owner.get(key);
            if (current === undefined || bucket.path < current) {
                owner.set(key, bucket.path);
            }
        }
    }
    return owner;
}

/**
 * Keeps only the facts this bucket canonically owns; among within-bucket duplicates of an
 * id (declaration merging — e.g. two `interface Shared` in one file mint the same node id),
 * keeps the LAST occurrence. Returns the original array untouched when nothing was dropped
 * (the overwhelmingly common no-duplicates case). Mirrors CSharpWorkspaceAnalyzer.FilterOwned.
 */
function filterOwned(items, idOf, owner, bucketPath) {
    const lastIndex = new Map();
    for (let i = 0; i < items.length; i++) {
        lastIndex.set(idOf(items[i]), i);
    }
    const kept = [];
    for (let i = 0; i < items.length; i++) {
        const key = idOf(items[i]);
        if (lastIndex.get(key) === i && owner.get(key) === bucketPath) {
            kept.push(items[i]);
        }
    }
    return kept.length === items.length ? items : kept;
}

/**
 * Deterministic, length-and-type-framed serialization of any emitted fact value. Every
 * string carries a length prefix, every object emits sorted keys, and each type gets a
 * distinct tag, so two distinct fact sets can never collide by field-boundary ambiguity.
 * This is the JS analog of the C# FactHasher's null/present + length framing.
 */
function stableSerialize(value) {
    if (value === undefined) return 'u';
    if (value === null) return 'n';
    if (typeof value === 'string') return `s${value.length}:${value}`;
    if (typeof value === 'number') return `i${value}`;
    if (typeof value === 'boolean') return `b${value ? 1 : 0}`;
    if (Array.isArray(value)) {
        return `a${value.length}[${value.map(stableSerialize).join(',')}]`;
    }
    const keys = Object.keys(value).sort();
    return `o${keys.length}{${keys.map(k => `${stableSerialize(k)}=${stableSerialize(value[k])}`).join(',')}}`;
}

/**
 * SHA-256 content hash of one file's (deduped) bucket, stable across processes. Nodes are
 * sorted by id and edges by id first so the hash is independent of walk order, then every
 * emission-affecting field of each fact is folded in with unambiguous framing. Hex string.
 * Mirrors CSharpWorkspaceAnalyzer.ComputeBucketHash.
 */
function computeBucketHash(bucket) {
    const byId = (a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
    const nodes = [...bucket.nodes].sort(byId);
    const edges = [...bucket.edges].sort(byId);
    const hash = crypto.createHash('sha256');
    hash.update(`N${nodes.length};`);
    for (const node of nodes) hash.update(`${stableSerialize(node)};`);
    hash.update(`E${edges.length};`);
    for (const edge of edges) hash.update(`${stableSerialize(edge)};`);
    return hash.digest('hex');
}

/**
 * Walks the whole workspace into per-file buckets (cross-file binding correctness requires
 * the full walk; the language service makes unchanged-file re-walks cheap), then applies
 * duplicate-id canonicalization so the deduped buckets feed BOTH the flat whole-workspace
 * emission and the per-file hashes by construction. Returns { buckets, diagnostics }.
 */
async function analyzeWorkspace(workspace, workspaceId, token, progressContext = null) {
    const program = workspace.service.getProgram();
    const checker = program.getTypeChecker();

    const diagnostics = [];
    if (workspace.configDiagnostic) {
        diagnostics.push(workspace.configDiagnostic);
    }

    workspace.beginAnalysis();
    const nuxtConfig = ['nuxt.config.ts', 'nuxt.config.js', 'nuxt.config.mjs']
        .map(name => path.join(workspace.rootPath, name)).find(candidate => fs.existsSync(candidate));
    if (nuxtConfig && !NUXT_COMPONENT_DECLARATIONS.some(
        relative => fs.existsSync(path.join(workspace.rootPath, relative)))) {
        diagnostics.push({
            filePath: nuxtConfig,
            severity: 'warning',
            message: 'Nuxt has not generated .nuxt/components.d.ts (run `nuxi prepare`), so a component '
                + 'a template renders without importing it is not bound.',
        });
    }

    const rawBuckets = [];
    const pendingTags = [];
    const registrations = [];
    const files = workspace.filePaths();
    const total = files.length;
    const progressInterval = Math.max(1, Math.ceil(total / 100));
    if (total === 0 && progressContext) {
        await emitAnalysisProgress(progressContext.requestId, workspaceId,
            progressContext.generation, 0, 0, null, token);
    }
    let processed = 0;
    for (const file of files) {
        throwIfCancelled(token);
        let result;
        try {
            diagnostics.push(...workspace.getEmbeddedDiagnostics(file));
            result = analyzeFile(workspace, workspaceId, program, checker, file, diagnostics);
        } catch (error) {
            diagnostics.push({
                filePath: file,
                severity: 'warning',
                message: `File analysis failed and was skipped: ${error.message}`,
            });
            result = { nodes: [], edges: [] };
        }
        // bucket.path is the verbatim filePaths() entry — the same string analyzeFile
        // stamped on each node's filePath and the same string ReplacesFiles must carry.
        rawBuckets.push({ path: file, nodes: result.nodes, edges: result.edges });
        const moduleId = result.nodes[0]?.id;
        for (const tag of result.unboundComponentTags ?? []) pendingTags.push({ ...tag, moduleId, edges: result.edges });
        for (const registration of result.componentRegistrations ?? []) registrations.push({ ...registration, path: file });
        processed++;
        if (progressContext && (processed === total || processed % progressInterval === 0)) {
            await emitAnalysisProgress(progressContext.requestId, workspaceId,
                progressContext.generation, processed, total, file, token);
        }
        await breathe(); // let $/cancel land between files
    }

    // A component registered on the application is every template's. Of two
    // registrations of one name, the file that sorts first wins, whatever was edited last.
    if (pendingTags.length > 0 && registrations.length > 0) {
        const registered = new Map();
        for (const registration of [...registrations].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))) {
            if (!registered.has(registration.name)) registered.set(registration.name, registration.targetId);
        }
        for (const tag of pendingTags) {
            const name = tag.names.find(candidate => registered.has(candidate));
            if (name) tag.edges.push(makeEdge(tag.moduleId, registered.get(name), 'CALLS', tag.metadata));
        }
    }

    const nodeOwner = computeOwners(rawBuckets, b => b.nodes, n => n.id);
    const edgeOwner = computeOwners(rawBuckets, b => b.edges, e => e.id);

    const buckets = rawBuckets.map(raw => ({
        path: raw.path,
        nodes: filterOwned(raw.nodes, n => n.id, nodeOwner, raw.path),
        edges: filterOwned(raw.edges, e => e.id, edgeOwner, raw.path),
    }));

    return { buckets, diagnostics };
}

async function emitAnalysisProgress(requestId, workspaceId, generation,
    filesProcessed, filesTotal, currentFile, token) {
    throwIfCancelled(token);
    await notify('analysis/progress', {
        parserId: PARSER_ID,
        parserVersion: PARSER_VERSION,
        workspaceId,
        generation,
        requestId,
        filesProcessed,
        filesTotal,
        currentFile,
    });
}

/**
 * Streams a node/edge set as one or more analysis/delta notifications, chunked at
 * MAX_ITEMS_PER_DELTA. Always emits at least one delta (the supervisor requires ≥1 per
 * mutation), so an empty incremental batch still sends a single terminal delta carrying
 * only its replacesFiles. Diagnostics ride on the first chunk only.
 */
async function emitDeltas(requestId, workspaceId, generation, nodes, edges, replacesWorkspace, replacesFiles, diagnostics, token) {
    const chunk = (items, size) => {
        const chunks = [];
        for (let offset = 0; offset < items.length; offset += size) {
            chunks.push(items.slice(offset, offset + size));
        }
        return chunks;
    };
    const nodeChunks = chunk(nodes, MAX_ITEMS_PER_DELTA);
    const edgeChunks = chunk(edges, MAX_ITEMS_PER_DELTA);
    const totalChunks = Math.max(1, nodeChunks.length + edgeChunks.length);

    for (let i = 0; i < totalChunks; i++) {
        throwIfCancelled(token);
        const edgeIndex = i - nodeChunks.length;
        await notify('analysis/delta', {
            parserId: PARSER_ID,
            parserVersion: PARSER_VERSION,
            workspaceId,
            generation,
            requestId,
            replacesWorkspace,
            replacesFiles: replacesWorkspace ? [] : replacesFiles,
            nodes: i < nodeChunks.length ? nodeChunks[i] : [],
            edges: edgeIndex >= 0 && edgeIndex < edgeChunks.length ? edgeChunks[edgeIndex] : [],
            isLastForRequest: i === totalChunks - 1,
            diagnostics: i === 0 && diagnostics.length > 0 ? diagnostics : undefined,
        });
    }
    return totalChunks;
}

// ---------------------------------------------------------------------------
// Request handlers
// ---------------------------------------------------------------------------

function requireParams(params) {
    if (!params) {
        const error = new Error('Missing params.');
        error.rpcCode = ErrorCodes.InvalidParams;
        throw error;
    }
    return params;
}

function getWorkspace(workspaceId) {
    let workspace = workspaces.get(workspaceId);
    if (!workspace) {
        workspace = new Workspace(hostRootPath);
        workspaces.set(workspaceId, workspace);
    }
    return workspace;
}

handlers.set('initialize', async (_id, params) => {
    const initialize = requireParams(params);
    hostRootPath = initialize.rootPath || process.cwd();
    return {
        parserId: PARSER_ID,
        parserVersion: PARSER_VERSION,
        displayName: 'TypeScript',
        protocolVersion: PROTOCOL_VERSION,
        languages: ['typescript', 'javascript'],
        extensions: SOURCE_EXTENSIONS,
        projectMarkers: ['tsconfig.json', 'package.json'],
        capabilities: {
            workspaceAnalysis: true,
            incrementalUpdates: true,
            semanticAnalysis: true,
            nativeSyntaxTree: true,
        },
        // Positions are 1-based; end positions come from ts.Node.getEnd(), which is
        // one past the last character — i.e. exclusive.
        spanSemantics: { lineBase: 1, columnBase: 1, endIsInclusive: false },
    };
});

handlers.set('workspace/open', async (_id, params) => {
    const open = requireParams(params);
    const workspace = getWorkspace(open.workspaceId);
    if (open.rootPath) workspace.setRootPath(open.rootPath);
    workspace.syncApproved(open.approvedFiles || []);
    return { workspaceId: open.workspaceId, opened: true };
});

handlers.set('workspace/index', async (id, params, token) => {
    const index = requireParams(params);
    const workspace = getWorkspace(index.workspaceId);
    workspace.replaceFiles(index.files || []);
    // Whole-workspace (index/reset) path: walk everything, seed the per-file hash
    // baseline, and replace the whole workspace (replacesWorkspace:true, empty
    // replacesFiles). The deduped buckets feed both the flat emission and the seed.
    const { buckets, diagnostics } = await analyzeWorkspace(
        workspace, index.workspaceId, token, { requestId: id, generation: index.generation });
    const nodes = [];
    const edges = [];
    for (const bucket of buckets) {
        nodes.push(...bucket.nodes);
        edges.push(...bucket.edges);
    }
    const deltasEmitted = await emitDeltas(
        id, index.workspaceId, index.generation, nodes, edges, true, [], diagnostics, token);
    // Seed only after every chunk was sent, mirroring the incremental path's
    // commit-after-send rule: a cancelled mid-index emission must not leave the
    // hash baseline claiming facts the host never received.
    workspace.seedFactHashes(buckets);
    return {
        workspaceId: index.workspaceId,
        generation: index.generation,
        deltasEmitted,
        complete: true,
    };
});

handlers.set('workspace/applyChanges', async (id, params, token) => {
    const apply = requireParams(params);
    const workspace = getWorkspace(apply.workspaceId);
    workspace.applyChanges(apply.changes || []);
    // Incremental path: re-walk the whole workspace for cross-file binding correctness
    // (a one-file edit can change semantic resolution in untouched dependents — the
    // language service reuses their SourceFiles, so this does not respawn Node or reparse
    // unchanged snapshots), then hash-diff the freshly walked buckets against the last
    // emission and send ONLY the dirty buckets' facts, scoped to dirty ∪ removed
    // (replacesWorkspace:false, verbatim replacesFiles). Cross-file rebinds surface for
    // free: an untouched dependent whose edge target changed hashes differently and joins
    // the dirty set. The hash map is committed only after every chunk is accepted, so a
    // failed send cannot desync it.
    const { buckets, diagnostics } = await analyzeWorkspace(workspace, apply.workspaceId, token);
    const emission = workspace.buildIncrementalEmission(buckets);
    const deltasEmitted = await emitDeltas(
        id, apply.workspaceId, apply.generation,
        emission.nodes, emission.edges, false, emission.replacesFiles, diagnostics, token);
    workspace.commitIncremental(emission);
    return {
        workspaceId: apply.workspaceId,
        generation: apply.generation,
        deltasEmitted,
        complete: true,
    };
});

handlers.set('syntaxTree/get', async (_id, params, token) => {
    const request = requireParams(params);
    const workspace = workspaces.get(request.workspaceId);
    if (!workspace) {
        const error = new Error(`Workspace '${request.workspaceId}' is not open.`);
        error.rpcCode = ErrorCodes.InvalidParams;
        throw error;
    }
    const fileName = path.resolve(request.filePath);
    if (!workspace.hasFile(fileName)) {
        const error = new Error('The file is not open in this workspace.');
        error.rpcCode = ErrorCodes.InvalidParams;
        throw error;
    }
    const maxDepth = request.maxDepth === undefined ? 8 : request.maxDepth;
    if (!Number.isInteger(maxDepth) || maxDepth < 0 || maxDepth > 32
        || (request.start === undefined) !== (request.length === undefined)) {
        const error = new Error('start/length must be supplied together and maxDepth must be between 0 and 32.');
        error.rpcCode = ErrorCodes.InvalidParams;
        throw error;
    }

    const program = workspace.service.getProgram();
    const sourceFile = program?.getSourceFiles().find(file => canonicalKey(file.fileName) === canonicalKey(fileName));
    if (!sourceFile) {
        const error = new Error('The file is not available in the TypeScript program.');
        error.rpcCode = ErrorCodes.InvalidParams;
        throw error;
    }
    let selected = sourceFile;
    if (request.start !== undefined) {
        const start = request.start;
        const length = request.length;
        if (!Number.isInteger(start) || !Number.isInteger(length)
            || start < 0 || length < 0 || start + length > sourceFile.end) {
            const error = new Error('The requested range is outside the file.');
            error.rpcCode = ErrorCodes.InvalidParams;
            throw error;
        }
        selected = smallestContainingNode(sourceFile, start, start + length);
    }

    const state = { maxDepth, maxNodes: 10000, nodesWritten: 0, truncated: false, token };
    return {
        parserId: PARSER_ID,
        parserVersion: PARSER_VERSION,
        workspaceId: request.workspaceId,
        filePath: fileName,
        format: 'typescript-compiler-syntax-v1',
        tree: serializeNativeNode(selected, sourceFile, 0, state),
        truncated: state.truncated,
    };
});

function smallestContainingNode(root, start, end) {
    let selected = root;
    const visit = node => {
        if (node.pos <= start && node.end >= end) {
            selected = node;
            ts.forEachChild(node, visit);
        }
    };
    ts.forEachChild(root, visit);
    return selected;
}

function serializeNativeNode(node, sourceFile, depth, state) {
    throwIfCancelled(state.token);
    state.nodesWritten++;
    const result = {
        kind: ts.SyntaxKind[node.kind],
        rawKind: node.kind,
        pos: node.pos,
        start: node.getStart(sourceFile),
        end: node.end,
        flags: node.flags,
    };
    const children = node.getChildren(sourceFile);
    if (children.length === 0) {
        if (node.kind >= ts.SyntaxKind.FirstToken && node.kind <= ts.SyntaxKind.LastToken) {
            const text = node.getText(sourceFile);
            result.text = text.length > 4096 ? text.substring(0, 4096) : text;
            if (text.length > 4096) result.textTruncated = true;
        }
        return result;
    }
    if (depth >= state.maxDepth || state.nodesWritten >= state.maxNodes) {
        state.truncated = true;
        result.childrenTruncated = true;
        return result;
    }
    result.children = [];
    for (const child of children) {
        if (state.nodesWritten >= state.maxNodes) {
            state.truncated = true;
            result.childrenTruncated = true;
            break;
        }
        result.children.push(serializeNativeNode(child, sourceFile, depth + 1, state));
    }
    return result;
}

handlers.set('shutdown', async () => null);

startReadLoop();
