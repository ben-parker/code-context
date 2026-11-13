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
// spelled. A refused path looks like a missing one, so no caller needs a check of its own.
//
// A link is how a path on a drive reaches a share after all. Package managers link
// packages into node_modules, so a link inside the repository is followed, but only to a
// place that would itself be read: the link is read, never opened, to find out. A path can
// name a link without spelling its name, by a short name (LONGLI~1), by a stream of it
// (link::$INDEX_ALLOCATION) or through a second name of a directory above the repository.
// A stream is refused wherever it stands, a name that could be a second name is asked
// about, and second names above the repository are written out before the path is judged.
//
// A device's reserved name (CON, NUL, COM1) is refused as the last name of a path. Older
// Windows opens the device there, wherever the path stands and whatever extension follows,
// and reading a console or a port never returns; newer Windows does so only for the bare
// name. Which one runs here is not asked: the name is refused on both.
//
// Off Windows nothing is refused and install() changes nothing.
//
// The decisions are shared with the C# worker's PathGuard through
// tests/Shared/disk-guard-vectors.json, which both are tested against.

const fs = require('fs');
const path = require('path');

const B = String.fromCharCode(92);
const NT_PREFIX = B + '??' + B;
const MAX_LINK_HOPS = 40;
// Superscript digits name the first three ports as well.
const DEVICE_NAME = /^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[0-9\u00b9\u00b2\u00b3])$/i;

/**
 * Creates a guard. `platform` and `io` exist for tests. Nothing in `io` follows a link:
 * `io.links(directory)` returns the lower-cased names in a directory that may be links
 * (null, or an error, when it cannot be listed), `io.lstat(path)` the stats of a path asked for by the
 * name given (undefined when nothing answers to it), `io.readlink(path)` the text of a
 * link, and `io.expand(path)` a path outside every repository as the disk spells it.
 */
function createGuard({ platform = process.platform, io } = {}) {
    const windows = platform === 'win32';
    const REFUSED = Symbol('refused');
    const MISSING = Symbol('missing');
    const shares = new Set();
    // Each repository, lower-cased, as the host spelled it and as the disk does.
    const roots = [];
    // What was learned of the disk, until forget(), each under the spelling that was asked
    // about: a directory can be made to tell `pkg` from `PKG`, so two spellings are two
    // questions. Only names within one listing are matched without regard to case, which
    // can only ask the disk about more of them.
    const listings = new Map();
    const followed = new Map();
    const expansions = new Map();

    const key = text => text.toLowerCase();

    /** `host\share` of a plain network path; a device path (\\?\, \\.\) has none. */
    function shareOf(full) {
        if (!full.startsWith(B + B)) return null;
        const parts = full.slice(2).split(B);
        return parts.length >= 2 && parts[0].length > 0 && parts[1].length > 0 && parts[0] !== '?' && parts[0] !== '.'
            ? key(parts[0] + B + parts[1])
            : null;
    }

    function onDrive(full) {
        const letter = full.charCodeAt(0) | 0x20;
        return full.length >= 3 && letter >= 0x61 && letter <= 0x7a && full[1] === ':' && full[2] === B;
    }

    // A colon after the drive names a stream, and a stream of a directory is the directory by
    // another name, which no comparison of names would see. Nothing the worker reads has one.
    const allowedAsSpelled = full => !full.includes(':', 2) && (onDrive(full) || shares.has(shareOf(full)));

    /** Whether the last name of a path is a device's, as older Windows reads it: up to the first dot or colon, less trailing spaces. */
    function namesADevice(full) {
        const names = full.split(B).filter(Boolean);
        if (names.length === 0) return false;
        const stem = names[names.length - 1].split(/[.:]/, 1)[0].replace(/ +$/, '');
        return DEVICE_NAME.test(stem);
    }

    /** The path as text, or null for what names no file here (a descriptor, say). */
    function textOf(value) {
        if (typeof value === 'string') return value;
        if (ArrayBuffer.isView(value)) return Buffer.from(value.buffer, value.byteOffset, value.byteLength).toString('utf8');
        if (value && typeof value === 'object' && typeof value.href === 'string' && typeof value.protocol === 'string') {
            let url;
            try {
                url = new URL(value.href);
            } catch {
                return '';
            }
            if (url.protocol !== 'file:') return null;
            // file://host/share/x is \\host\share\x to Windows.
            const local = decodeURIComponent(url.pathname).split('/').join(B);
            return url.hostname ? B + B + url.hostname + local : local.slice(1);
        }
        return null;
    }

    /** Normalized without a look at the disk, or null for what cannot name a file. */
    function lexical(text) {
        if (text.length === 0 || text.includes('\0')) return null;
        return path.win32.resolve(text);
    }

    const isRootOrBelow = full => {
        const lowered = key(full);
        return roots.some(root => lowered === root.key || lowered.startsWith(root.prefix));
    };

    /** The repository a path is below, not the repository itself. */
    function rootAbove(full) {
        const lowered = key(full);
        let found;
        for (const root of roots) {
            if (lowered.length > root.prefix.length && lowered.startsWith(root.prefix)
                && (!found || root.prefix.length > found.prefix.length)) {
                found = root;
            }
        }
        return found;
    }

    const plainLetters = name => {
        for (let i = 0; i < name.length; i++) if (name.charCodeAt(i) > 0x7f) return false;
        return true;
    };

    /**
     * A short name, a name Windows trims before it looks, or a name with a letter past
     * ASCII: the file system matches those without regard to case by its own table, which is
     * not this language's, so only the file system can say what such a name names.
     */
    const couldBeASecondName = name => name.includes('~') || name.endsWith('.') || name.endsWith(' ') || !plainLetters(name);

    function expanded(spelled) {
        let known = expansions.get(spelled);
        if (known === undefined) {
            try {
                known = lexical(io.expand(spelled)) ?? spelled;
            } catch {
                known = spelled;
            }
            expansions.set(spelled, known);
        }
        return known;
    }

    /**
     * The path with the short names above the repository written out, so that a path which
     * enters the repository by one is seen to be inside it. Each is asked for only while the
     * path so far is outside the repository, where a link is the owner's.
     */
    function withoutShortNamesOutside(full) {
        if (roots.length === 0) return full;
        let prefix = path.win32.parse(full).root;
        const names = full.slice(prefix.length).split(B).filter(Boolean);
        if (!names.some(couldBeASecondName)) return full;
        for (let i = 0; i < names.length; i++) {
            let candidate = path.win32.join(prefix, names[i]);
            if (couldBeASecondName(names[i]) && !isRootOrBelow(candidate)) {
                candidate = expanded(candidate);
                // The owner's own link to a share: theirs, and taken as it is.
                if (!allowedAsSpelled(candidate)) return full;
            }
            if (isRootOrBelow(candidate)) return path.win32.join(candidate, ...names.slice(i + 1));
            prefix = candidate;
        }
        return prefix;
    }

    function linksIn(directory) {
        let names = listings.get(directory);
        if (names === undefined) {
            try {
                names = io.links(directory);
                // A link with a letter past ASCII may answer to a name of plain letters.
                if (names) names.anyName = [...names].some(name => !plainLetters(name));
            } catch {
                names = null;
            }
            listings.set(directory, names);
        }
        return names;
    }

    /** One name in a directory that is itself real: the entry, where its link leads, REFUSED or MISSING. */
    function step(directory, name, hops) {
        const names = linksIn(directory);
        const entry = path.win32.join(directory, name);
        // A directory that cannot be listed says nothing, so each name in it is asked about.
        if (names !== null && !names.has(key(name)) && !names.anyName && !couldBeASecondName(name)) return entry;
        let result = followed.get(entry);
        if (result === undefined) {
            result = follow(entry, hops);
            followed.set(entry, result);
        }
        return result;
    }

    function follow(entry, hops) {
        let stats;
        try {
            stats = io.lstat(entry);
        } catch {
            // It is there and will not say what it is.
            return REFUSED;
        }
        if (!stats) return MISSING;
        // A reparse point of another kind, such as a file kept in the cloud, leads nowhere else.
        if (!stats.isSymbolicLink()) return entry;
        if (hops >= MAX_LINK_HOPS) return REFUSED;
        try {
            const text = io.readlink(entry);
            // What is left in the system's own form names a device, which this cannot judge.
            if (typeof text !== 'string' || text.startsWith(NT_PREFIX)) return REFUSED;
            const target = lexical(path.win32.resolve(path.win32.dirname(entry), text));
            return target && allowedAsSpelled(target) ? resolve(target, hops + 1) : REFUSED;
        } catch {
            return REFUSED;
        }
    }

    /** Where a path that is allowed as spelled really leads, REFUSED or MISSING. */
    function resolve(full, hops) {
        const spelled = withoutShortNamesOutside(full);
        const root = rootAbove(spelled);
        if (!root) return spelled;
        let real = spelled.slice(0, root.key.length);
        const names = spelled.slice(root.prefix.length).split(B).filter(Boolean);
        for (let i = 0; i < names.length; i++) {
            const next = step(real, names[i], hops);
            if (typeof next === 'symbol') return next;
            real = next;
            // A link led out of every repository. What is left of the path can lead back in,
            // so the whole of it is judged again from where the link leads.
            if (!isRootOrBelow(real)) {
                const rest = names.slice(i + 1);
                if (rest.length === 0) return real;
                return hops >= MAX_LINK_HOPS ? REFUSED : resolve(path.win32.join(real, ...rest), hops + 1);
            }
        }
        return real;
    }

    /** The one place that decides. */
    function refused(value) {
        if (!windows) return false;
        const text = textOf(value);
        if (text === null) return false;
        const full = lexical(text);
        if (!full || !allowedAsSpelled(full) || namesADevice(full)) return true;
        // Where a link leads is judged as well: a link can hold a device's name.
        const real = resolve(full, 0);
        return real === REFUSED || (typeof real === 'string' && namesADevice(real));
    }

    function forget() {
        listings.clear();
        followed.clear();
        expansions.clear();
    }

    /** Lets the share of a place the machine's owner chose be read. Never a place a repository names. */
    function allow(location) {
        if (!windows || typeof location !== 'string') return;
        const full = lexical(location);
        const share = full && shareOf(full);
        if (share) shares.add(share);
    }

    /** A repository: its share may be read, and links inside it are followed only to allowed places. */
    function addRoot(location) {
        if (!windows || typeof location !== 'string') return;
        const full = lexical(location);
        if (!full) return;
        allow(full);
        // As the host spelled it and as the disk does: a path may come in either spelling.
        for (const spelling of allowedAsSpelled(full) ? [full, expanded(full)] : [full]) {
            const prefix = key(spelling.endsWith(B) ? spelling : spelling + B);
            if (roots.some(root => root.prefix === prefix)) continue;
            roots.push({ key: prefix.length > 3 ? prefix.slice(0, -1) : prefix, prefix });
        }
        forget();
    }

    return { refused, allow, addRoot, forget };
}

function absent(name, value) {
    const shown = typeof value === 'string' ? value : ArrayBuffer.isView(value) ? Buffer.from(value).toString('utf8') : String(value);
    const error = new Error(`ENOENT: no such file or directory, ${name} '${shown}'`);
    error.code = 'ENOENT';
    error.errno = -4058;
    error.syscall = name;
    error.path = shown;
    return error;
}

// The functions whose second argument is a path as well.
const TWO_PATHS = new Set(['rename', 'copyFile', 'cp', 'link', 'symlink']);
// The functions of `fs` that answer at once and not through a callback.
const ANSWERS_AT_ONCE = new Set(['createReadStream', 'createWriteStream', 'watch', 'watchFile', 'unwatchFile']);

let installed;

/**
 * Puts the guard under `fs`, `fs.promises` and their ES module exports, and returns it.
 * Every function of `fs` is wrapped, so one added to Node later is guarded as well; a call
 * whose first argument is not a path (a descriptor) passes through untouched.
 *
 * `require` and `import` do not come through `fs`. The worker loads only its own modules
 * by name, never a path a repository gives it, and a test holds it to that.
 */
function install() {
    if (installed) return installed;
    const original = {
        lstat: fs.lstatSync,
        readlink: fs.readlinkSync,
        readdir: fs.readdirSync,
        realpath: fs.realpathSync.native,
    };
    const guard = createGuard({
        io: {
            links: directory => new Set(original.readdir.call(fs, directory, { withFileTypes: true })
                .filter(entry => entry.isSymbolicLink())
                .map(entry => entry.name.toLowerCase())),
            lstat: entry => original.lstat.call(fs, entry, { throwIfNoEntry: false }),
            readlink: entry => original.readlink.call(fs, entry),
            expand: entry => original.realpath.call(fs, entry),
        },
    });
    installed = guard;
    if (process.platform !== 'win32') return guard;

    /** `style` says how a function answers: 'sync', 'callback' or 'promise'. */
    const wrappers = new Map();
    const wrap = (name, fn, style) => {
        // A function can hang off itself, or off two others.
        if (wrappers.has(fn)) return wrappers.get(fn);
        const base = name.replace(/Sync$/, '');
        const wrapped = function (...args) {
            const bad = guard.refused(args[0]) ? args[0]
                : TWO_PATHS.has(base) && guard.refused(args[1]) ? args[1]
                    : undefined;
            if (bad === undefined) return fn.apply(this, args);
            const callback = style === 'callback' && typeof args[args.length - 1] === 'function' ? args[args.length - 1] : null;
            if (base === 'exists') {
                if (style === 'promise') return Promise.resolve(false);
                if (callback) process.nextTick(callback, false);
                return callback ? undefined : false;
            }
            const error = absent(base, bad);
            if (style === 'promise') return Promise.reject(error);
            if (callback) {
                process.nextTick(callback, error);
                return undefined;
            }
            if ((base === 'stat' || base === 'lstat') && args[1] && args[1].throwIfNoEntry === false) return undefined;
            throw error;
        };
        wrappers.set(fn, wrapped);
        // What hangs off a function is guarded too: realpathSync.native, and the form
        // util.promisify takes, which answers with a promise.
        for (const property of Reflect.ownKeys(fn)) {
            if (property === 'length' || property === 'name' || property === 'prototype') continue;
            const descriptor = Object.getOwnPropertyDescriptor(fn, property);
            if (typeof descriptor.value === 'function') {
                descriptor.value = wrap(name, descriptor.value, typeof property === 'symbol' ? 'promise' : style);
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

    guardAll(fs.promises, name => (name === 'watch' ? 'sync' : 'promise'));
    guardAll(fs, name => (name === 'openAsBlob' ? 'promise'
        : name.endsWith('Sync') || ANSWERS_AT_ONCE.has(name) ? 'sync' : 'callback'));
    require('module').syncBuiltinESMExports();
    return guard;
}

module.exports = { install, createGuard };
