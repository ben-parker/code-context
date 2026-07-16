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
// workspace per watched root for now). If <root>/tsconfig.json exists its
// compilerOptions drive module resolution; otherwise permissive defaults
// (allowJs, esnext) are used. Approved files are the program roots — tsconfig
// include/exclude does not override the host's approved set.

const fs = require('fs');
const path = require('path');

let ts;
try {
    ts = require('typescript');
} catch (error) {
    process.stderr.write(
        'typescript-worker: cannot load the "typescript" package. ' +
        'Run "npm install" next to typescript-worker.js.\n');
    process.exit(2);
}

const PROTOCOL_VERSION = 1;
const PARSER_ID = 'typescript';
const PARSER_VERSION = (() => {
    try {
        return require(path.join(__dirname, 'package.json')).version || '1.0.0';
    } catch {
        return '1.0.0';
    }
})();
const MAX_ITEMS_PER_DELTA = 1000;

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

class Workspace {
    constructor(rootPath) {
        this.rootPath = rootPath;
        this.files = new Map(); // canonicalKey -> { path, version }
        const loaded = loadCompilerOptions(rootPath);
        this.compilerOptions = loaded.options;
        this.configDiagnostic = loaded.diagnostic;
        const self = this;
        this.serviceHost = {
            getScriptFileNames: () => [...self.files.values()].map(f => f.path),
            getScriptVersion: fileName => String(self.files.get(canonicalKey(fileName))?.version ?? 0),
            getScriptSnapshot: fileName => {
                try {
                    return ts.ScriptSnapshot.fromString(fs.readFileSync(fileName, 'utf8'));
                } catch {
                    return undefined;
                }
            },
            getCurrentDirectory: () => self.rootPath,
            getCompilationSettings: () => self.compilerOptions,
            getDefaultLibFileName: options => ts.getDefaultLibFilePath(options),
            fileExists: ts.sys.fileExists,
            readFile: ts.sys.readFile,
            readDirectory: ts.sys.readDirectory,
            directoryExists: ts.sys.directoryExists,
            getDirectories: ts.sys.getDirectories,
        };
        this.service = ts.createLanguageService(this.serviceHost, ts.createDocumentRegistry());
    }

    hasFile(p) {
        return this.files.has(canonicalKey(p));
    }

    /** Re-roots the workspace (and reloads tsconfig options) if the host opens it
     * with a different root than the one initialize carried. */
    setRootPath(rootPath) {
        const resolved = path.resolve(rootPath);
        if (canonicalKey(resolved) === canonicalKey(this.rootPath)) return;
        this.rootPath = resolved;
        const loaded = loadCompilerOptions(resolved);
        this.compilerOptions = loaded.options;
        this.configDiagnostic = loaded.diagnostic;
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
            if (!approved.has(known)) this.files.delete(known);
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
    }

    applyChanges(changes) {
        const touched = [];
        for (const change of changes) {
            touched.push(path.resolve(change.path));
            if (change.changeType === 'deleted') {
                this.files.delete(canonicalKey(change.path));
            } else {
                if (change.changeType === 'renamed' && change.oldPath) {
                    this.files.delete(canonicalKey(change.oldPath));
                    // The old path must be inside the delta's replacement scope or
                    // the renamed file's previous facts would linger in the graph.
                    touched.push(path.resolve(change.oldPath));
                }
                this.bump(change.path);
            }
        }
        return touched;
    }
}

function loadCompilerOptions(rootPath) {
    const defaults = {
        allowJs: true,
        checkJs: false,
        target: ts.ScriptTarget.Latest,
        module: ts.ModuleKind.ESNext,
        moduleResolution: ts.ModuleResolutionKind.Bundler,
        jsx: ts.JsxEmit.Preserve,
        skipLibCheck: true,
        noEmit: true,
    };
    const configPath = path.join(rootPath, 'tsconfig.json');
    const fallbackDiagnostic = message => ({
        options: defaults,
        diagnostic: {
            filePath: configPath,
            severity: 'warning',
            message: `tsconfig.json could not be used (${message}); indexing with permissive defaults.`,
        },
    });
    try {
        if (fs.existsSync(configPath)) {
            const read = ts.readConfigFile(configPath, ts.sys.readFile);
            if (read.error) {
                return fallbackDiagnostic(ts.flattenDiagnosticMessageText(read.error.messageText, ' '));
            }
            const parsed = ts.parseJsonConfigFileContent(read.config, ts.sys, rootPath);
            return { options: { ...defaults, ...parsed.options, noEmit: true }, diagnostic: undefined };
        }
    } catch (error) {
        return fallbackDiagnostic(error.message);
    }
    return { options: defaults, diagnostic: undefined };
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

/**
 * Builds the container-qualified name of a declaration inside its file
 * (namespaces + class/interface chain + own name), used for stable IDs.
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
        } else if (ts.isFunctionDeclaration(current) && current.name) {
            parts.unshift(`${current.name.getText(sourceFile)}${parameterKey(current, sourceFile)}`);
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

    /** Resolves a name expression to a declaration inside the project, if any. */
    const resolveToProjectDeclaration = expression => {
        try {
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
        // Line AND column in the ID: two same-target calls on one line must not
        // silently collapse into a single edge.
        const suffix = metadata && metadata.line
            ? `@${metadata.line}:${metadata.column ?? '0'}`
            : '';
        edges.push({
            id: `${sourceId}=[${kind}]=>${targetId}${suffix}`,
            sourceId,
            targetId,
            kind,
            metadata: metadata && Object.keys(metadata).length > 0 ? metadata : undefined,
        });
    };

    /** Nearest enclosing emitted declaration to use as a CALLS source. */
    const enclosingDeclarationId = tsNode => {
        let current = tsNode.parent;
        while (current && current !== sourceFile) {
            if (ts.isMethodDeclaration(current) || ts.isConstructorDeclaration(current)
                || ts.isFunctionDeclaration(current) || ts.isGetAccessor(current)
                || ts.isSetAccessor(current)) {
                return nodeId(qualifiedNameOf(current, sourceFile));
            }
            if (ts.isVariableDeclaration(current) && current.initializer
                && ts.isArrowFunction(current.initializer)) {
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
                }
                break;
            }
            case ts.SyntaxKind.VariableStatement: {
                for (const declaration of tsNode.declarationList?.declarations ?? []) {
                    if (declaration.name && declaration.initializer
                        && ts.isArrowFunction(declaration.initializer)) {
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
                    if (container && (ts.isClassDeclaration(container) || ts.isInterfaceDeclaration(container))
                        && container.name) {
                        addEdge(nodeId(qualifiedNameOf(container, sourceFile)), methodNode.id, 'HAS_METHOD');
                    }
                }
                break;
            }
            case ts.SyntaxKind.Constructor: {
                const ctorNode = addNode('constructor', 'Method', tsNode);
                const container = tsNode.parent;
                if (container && ts.isClassDeclaration(container) && container.name) {
                    addEdge(nodeId(qualifiedNameOf(container, sourceFile)), ctorNode.id, 'HAS_METHOD');
                }
                break;
            }
            case ts.SyntaxKind.PropertyDeclaration: {
                if (tsNode.name) {
                    const propertyNode = addNode(tsNode.name.getText(sourceFile), 'Field', tsNode);
                    const container = tsNode.parent;
                    if (container && ts.isClassDeclaration(container) && container.name) {
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
                    if (container && ts.isClassDeclaration(container) && container.name) {
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
            case ts.SyntaxKind.ImportDeclaration: {
                if (tsNode.moduleSpecifier) {
                    const moduleName = tsNode.moduleSpecifier.getText(sourceFile).replace(/['"]/g, '');
                    const resolved = resolveToProjectDeclaration(tsNode.moduleSpecifier);
                    const targetId = resolved
                        ? `${graphPrefix}${relPath(root, resolved.getSourceFile().fileName)}`
                        : moduleName;
                    const metadata = { moduleSpecifier: moduleName, isFileImport: 'true' };
                    if (!resolved) metadata.unresolved = 'true'; // external package or unresolvable path
                    addEdge(`${graphPrefix}${fileRel}`, targetId, 'IMPORTS', metadata);
                }
                break;
            }
            case ts.SyntaxKind.CallExpression: {
                const expression = tsNode.expression;
                if (expression) {
                    const nameNode = ts.isPropertyAccessExpression(expression) ? expression.name : expression;
                    const callTarget = nameNode.getText(sourceFile);
                    if (callTarget) {
                        const position = sourceFile.getLineAndCharacterOfPosition(tsNode.getStart(sourceFile));
                        const targetDecl = resolveToProjectDeclaration(nameNode);
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
                break;
            }
        }
        ts.forEachChild(tsNode, visit);
    };

    visit(sourceFile);
    return { nodes, edges };
}

// ---------------------------------------------------------------------------
// Delta publication
// ---------------------------------------------------------------------------

async function publishDeltas(requestId, workspaceId, generation, workspace, filesToEmit, replacesWorkspace, replacesFiles, token) {
    const program = workspace.service.getProgram();
    const checker = program.getTypeChecker();

    const nodes = [];
    const edges = [];
    const diagnostics = [];
    if (workspace.configDiagnostic) {
        diagnostics.push(workspace.configDiagnostic);
    }
    for (const file of filesToEmit) {
        throwIfCancelled(token);
        try {
            const result = analyzeFile(workspace, workspaceId, program, checker, file, diagnostics);
            nodes.push(...result.nodes);
            edges.push(...result.edges);
        } catch (error) {
            diagnostics.push({
                filePath: file,
                severity: 'warning',
                message: `File analysis failed and was skipped: ${error.message}`,
            });
        }
        await breathe(); // let $/cancel land between files
    }

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
        extensions: ['.ts', '.tsx', '.js', '.jsx'],
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
    const deltasEmitted = await publishDeltas(
        id, index.workspaceId, index.generation, workspace,
        workspace.filePaths(), true, [], token);
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
    // A one-file edit can change semantic resolution in untouched dependents. The
    // language service retains and reuses their SourceFiles, but normalized facts
    // must be re-walked as a workspace replacement so stale cross-file edges cannot
    // survive. This does not respawn Node or reparse unchanged snapshots.
    const deltasEmitted = await publishDeltas(
        id, apply.workspaceId, apply.generation, workspace,
        workspace.filePaths(), true, [], token);
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
