'use strict';

// The worker's only route to the file system, as Projects/Disk.cs is for the C# worker.
//
// Reading a repository must never reach out to the network: opening \\host\share makes
// Windows authenticate to that host, which tells a repository who indexed it and hands it
// an NTLM exchange to use. A repository can name such a path in a tsconfig's `extends` or
// `references`, in a `paths` or bundler alias, and in any source file's import or
// `/// <reference>`, and TypeScript opens what it is given.
//
// So the guard sits under everything: install() replaces the functions of `fs` itself,
// before the compiler is loaded, and the compiler, the Vue compiler and the worker all
// reach the disk through them. On Windows a path is allowed if it is on a drive letter or
// on a share this instance was given, and everything else is refused, however it is
// spelled. A link inside the repository that leads to a refused place is refused too. A
// refused path looks like a missing one, so no caller needs a check of its own.
//
// Off Windows nothing is refused and install() changes nothing.

const fs = require('fs');
const path = require('path');

const BACKSLASH = String.fromCharCode(92);
const MAX_LINK_HOPS = 40;

/**
 * Creates a guard. `platform` and `io` exist for tests: `io.links(directory)` returns the
 * lower-cased names in a directory that may be links (null when it cannot be listed),
 * `io.lstat(path)` the stats of a path without following it (undefined when it is not
 * there) and `io.readlink(path)` the text of a link.
 */
function createGuard({ platform = process.platform, io } = {}) {
    const windows = platform === 'win32';
    const shares = new Set();
    const roots = [];
    // Directory (lower-cased) -> where it really is, REFUSED, or MISSING.
    const directories = new Map();
    // Directory (lower-cased) -> the names in it that may be links, from one listing: a
    // path is then checked without a look at the disk of its own.
    const listings = new Map();
    const REFUSED = Symbol('refused');
    const MISSING = Symbol('missing');

    const key = text => text.toLowerCase();

    /** `host\share` of a plain network path; a device path (\\?\, \\.\) has none. */
    function shareOf(full) {
        if (!full.startsWith(BACKSLASH + BACKSLASH)) return null;
        const parts = full.slice(2).split(BACKSLASH);
        return parts.length >= 2 && parts[0].length > 0 && parts[1].length > 0 && parts[0] !== '?' && parts[0] !== '.'
            ? key(parts[0] + BACKSLASH + parts[1])
            : null;
    }

    const onDrive = full => /^[A-Za-z]:\\/.test(full);

    /** The path as text, or null for what names no file here (a descriptor, say). */
    function textOf(value) {
        if (typeof value === 'string') return value;
        if (Buffer.isBuffer(value)) return value.toString('utf8');
        if (value instanceof URL) {
            if (value.protocol !== 'file:') return null;
            // file://host/share/x is \\host\share\x to Windows.
            const local = decodeURIComponent(value.pathname).split('/').join(BACKSLASH);
            return value.hostname ? BACKSLASH + BACKSLASH + value.hostname + local : local.replace(/^\\([A-Za-z]:)/, '$1');
        }
        return null;
    }

    /** Normalized without a look at the disk, or null for what cannot name a file. */
    function lexical(text) {
        if (text.length === 0 || text.includes('\0')) return null;
        return path.win32.resolve(text);
    }

    const allowedLexically = full => onDrive(full) || shares.has(shareOf(full));

    function rootHolding(full) {
        const lowered = key(full);
        return roots.find(root => lowered.startsWith(root.prefix));
    }

    /** Where a directory inside a repository really is: itself unless a link leads elsewhere. */
    function realDirectory(directory, hops) {
        const cached = directories.get(key(directory));
        if (cached !== undefined) return cached;
        let result;
        const root = rootHolding(directory + BACKSLASH);
        if (!root || key(directory) === root.key) {
            // Outside every repository, or the repository itself: the owner's, taken as it is.
            result = directory;
        } else {
            const parent = realDirectory(path.win32.dirname(directory), hops);
            result = typeof parent === 'symbol'
                ? parent
                : resolveEntry(path.win32.join(parent, path.win32.basename(directory)), hops);
        }
        directories.set(key(directory), result);
        return result;
    }

    /** A path whose parent is real: itself, where its link leads, REFUSED or MISSING. */
    function resolveEntry(entry, hops) {
        const directory = path.win32.dirname(entry);
        let names = listings.get(key(directory));
        if (names === undefined) {
            try {
                names = io.links(directory);
            } catch {
                names = null;
            }
            listings.set(key(directory), names);
        }
        if (names === null) return MISSING;
        if (!names.has(key(path.win32.basename(entry)))) return entry;
        let stats;
        try {
            stats = io.lstat(entry);
        } catch {
            return MISSING;
        }
        if (!stats) return MISSING;
        if (!stats.isSymbolicLink()) return entry;
        if (hops >= MAX_LINK_HOPS) return REFUSED;
        let target;
        try {
            target = lexical(path.win32.resolve(path.win32.dirname(entry), io.readlink(entry)));
        } catch {
            return REFUSED;
        }
        if (!target || !allowedLexically(target)) return REFUSED;
        // The link may lead to another one inside a repository.
        if (!rootHolding(target + BACKSLASH)) return target;
        const parent = realDirectory(path.win32.dirname(target), hops + 1);
        return typeof parent === 'symbol'
            ? parent
            : resolveEntry(path.win32.join(parent, path.win32.basename(target)), hops + 1);
    }

    /** The one place that decides. */
    function refused(value) {
        if (!windows) return false;
        const text = textOf(value);
        if (text === null) return false;
        const full = lexical(text);
        if (!full || !allowedLexically(full)) return true;
        const root = rootHolding(full);
        if (!root || key(full) === root.key) return false;
        const parent = realDirectory(path.win32.dirname(full), 0);
        if (parent === REFUSED) return true;
        if (parent === MISSING) return false;
        return resolveEntry(path.win32.join(parent, path.win32.basename(full)), 0) === REFUSED;
    }

    return {
        refused,
        /** Lets the share of a place the machine's owner chose be read. Never a place a repository names. */
        allow(location) {
            if (!windows || typeof location !== 'string' || location.length === 0) return;
            const full = lexical(location);
            const share = full && shareOf(full);
            if (share) shares.add(share);
        },
        /** A repository: its share may be read, and links inside it are followed only to allowed places. */
        addRoot(root) {
            if (!windows || typeof root !== 'string' || root.length === 0) return;
            const full = lexical(root);
            if (!full) return;
            this.allow(full);
            const trimmed = full.endsWith(BACKSLASH) ? full.slice(0, -1) : full;
            if (!roots.some(existing => existing.key === key(trimmed))) {
                roots.push({ key: key(trimmed), prefix: key(trimmed) + BACKSLASH });
                directories.clear();
                listings.clear();
            }
        },
        /** Forgets which directories are links; called before each request that reads the repository. */
        forget() {
            directories.clear();
            listings.clear();
        },
    };
}

function absent(name, value) {
    const shown = Buffer.isBuffer(value) ? value.toString('utf8') : String(value);
    const error = new Error(`ENOENT: no such file or directory, ${name} '${shown}'`);
    error.code = 'ENOENT';
    error.errno = -4058;
    error.syscall = name;
    error.path = shown;
    return error;
}

// The functions whose second argument is a path as well.
const TWO_PATHS = new Set(['rename', 'copyFile', 'cp', 'link', 'symlink']);

let installed;

/**
 * Puts the guard under `fs`, `fs.promises` and their ES module exports, and returns it.
 * Every function of `fs` is wrapped, so one added to Node later is guarded as well; a call
 * whose first argument is not a path (a descriptor) passes through untouched.
 */
function install() {
    if (installed) return installed;
    const original = {
        lstat: fs.lstatSync,
        readlink: fs.readlinkSync,
        readdir: fs.readdirSync,
    };
    const guard = createGuard({
        io: {
            links: directory => new Set(original.readdir.call(fs, directory, { withFileTypes: true })
                .filter(entry => entry.isSymbolicLink())
                .map(entry => entry.name.toLowerCase())),
            lstat: entry => original.lstat.call(fs, entry, { throwIfNoEntry: false }),
            readlink: entry => original.readlink.call(fs, entry),
        },
    });
    installed = guard;
    if (process.platform !== 'win32') return guard;

    const refusedArgument = (name, args) => {
        if (guard.refused(args[0])) return args[0];
        const base = name.replace(/Sync$/, '');
        return TWO_PATHS.has(base) && guard.refused(args[1]) ? args[1] : undefined;
    };

    const wrap = (name, fn, style) => {
        const wrapped = function (...args) {
            const bad = refusedArgument(name, args);
            if (bad === undefined) return fn.apply(this, args);
            const base = name.replace(/Sync$/, '');
            if (base === 'exists') {
                if (style === 'sync') return false;
                const callback = args[args.length - 1];
                if (typeof callback === 'function') process.nextTick(callback, false);
                return undefined;
            }
            const error = absent(base, bad);
            if (style === 'promise') return Promise.reject(error);
            if (style === 'callback') {
                const callback = args[args.length - 1];
                if (typeof callback === 'function') {
                    process.nextTick(callback, error);
                    return undefined;
                }
            }
            if (style === 'sync' && (base === 'stat' || base === 'lstat')) {
                if (args[1] && args[1].throwIfNoEntry === false) return undefined;
            }
            throw error;
        };
        // What hangs off a function (realpathSync.native, the promisify form) is kept or guarded.
        for (const property of Reflect.ownKeys(fn)) {
            if (property === 'length' || property === 'name' || property === 'prototype') continue;
            const descriptor = Object.getOwnPropertyDescriptor(fn, property);
            if (typeof descriptor.value === 'function' && typeof property === 'string') {
                descriptor.value = wrap(name, descriptor.value, style);
            }
            Object.defineProperty(wrapped, property, descriptor);
        }
        Object.defineProperty(wrapped, 'name', { value: fn.name });
        return wrapped;
    };

    const guardAll = (target, styleOf) => {
        for (const name of Object.keys(target)) {
            const descriptor = Object.getOwnPropertyDescriptor(target, name);
            // Classes (Stats, ReadStream) open nothing by themselves: their factories are guarded.
            if (!descriptor || typeof descriptor.value !== 'function' || /^[A-Z_]/.test(name)) continue;
            target[name] = wrap(name, descriptor.value, styleOf(name));
        }
    };

    const SYNCHRONOUS = new Set(['createReadStream', 'createWriteStream', 'watch', 'watchFile', 'unwatchFile']);
    guardAll(fs.promises, () => 'promise');
    guardAll(fs, name => (name.endsWith('Sync') || SYNCHRONOUS.has(name) ? 'sync' : 'callback'));
    require('module').syncBuiltinESMExports();
    return guard;
}

module.exports = { install, createGuard };
