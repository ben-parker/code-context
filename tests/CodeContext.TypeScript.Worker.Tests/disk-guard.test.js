'use strict';

// Run with `node --test`. TypeScriptWorkerDiskGuardTests runs this file in the
// ExternalTooling gate. The decisions are tested as Windows makes them on every
// platform: the guard takes its platform and its look at links as arguments.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const workerDirectory = path.join(__dirname, '..', '..', 'src', 'CodeContext.TypeScript.Worker');
const { createGuard, install } = require(path.join(workerDirectory, 'disk-guard.js'));

const B = String.fromCharCode(92);
const win = text => text.split('/').join(B);
const lower = text => text.toLowerCase();

const { scenarios } = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'Shared', 'disk-guard-vectors.json'), 'utf8'));

/** A guard on Windows over the made-up disk of a scenario, and every path it asked that disk about. */
function guardOver(scenario) {
    const links = new Map(Object.entries(scenario.links ?? {}).map(([link, text]) => [lower(link), text]));
    const aliases = new Map(Object.entries(scenario.aliases ?? {}).map(([alias, entry]) => [lower(alias), entry]));
    // A path as the disk spells it: each second name replaced by the entry it names.
    const real = spelled => {
        const root = path.win32.parse(spelled).root;
        let current = root;
        for (const name of spelled.slice(root.length).split(B).filter(Boolean)) {
            current = path.win32.join(current, name);
            current = aliases.get(lower(current)) ?? current;
        }
        return current;
    };
    const asked = [];
    const guard = createGuard({
        platform: 'win32',
        io: {
            links: directory => {
                asked.push(directory);
                const listed = lower(real(directory));
                return new Set([...links.keys()]
                    .filter(link => path.win32.dirname(link) === listed)
                    .map(link => path.win32.basename(link)));
            },
            lstat: entry => {
                asked.push(entry);
                return { isSymbolicLink: () => links.has(lower(real(entry))) };
            },
            readlink: entry => links.get(lower(real(entry))),
            expand: spelled => {
                asked.push(spelled);
                return real(spelled);
            },
        },
    });
    for (const location of scenario.allow ?? []) guard.allow(location);
    guard.addRoot(scenario.root);
    return { guard, asked };
}

for (const scenario of scenarios) {
    test(`vectors: ${scenario.name}`, () => {
        const { guard, asked } = guardOver(scenario);

        for (const { path: given, refused, why } of scenario.cases) {
            const expected = typeof refused === 'boolean' ? refused : refused.typescript;
            assert.equal(guard.refused(given), expected, `${why}: ${given}`);
        }
        // No share nobody chose was looked at to decide.
        assert.deepEqual(asked.filter(looked => lower(looked).startsWith(B + B + 'evil')), []);
    });
}

test('a share is refused when it is named by a URL or by bytes', () => {
    const { guard } = guardOver({ root: win('C:/repo') });

    assert.equal(guard.refused(new URL('file://evil/share/x.json')), true);
    assert.equal(guard.refused({ href: 'file://evil/share/x.json', protocol: 'file:' }), true);
    assert.equal(guard.refused(Buffer.from(win('//evil/share/x.json'))), true);
    assert.equal(guard.refused(new Uint8Array(Buffer.from(win('//evil/share/x.json')))), true);
    assert.equal(guard.refused(new URL('file:///C:/repo/a.ts')), false);
    assert.equal(guard.refused(Buffer.from(win('C:/repo/a.ts'))), false);
});

test('what cannot name a file is refused, and what is no path is left alone', () => {
    const { guard } = guardOver({ root: win('C:/repo') });

    assert.equal(guard.refused('C:/a\0b'), true);
    assert.equal(guard.refused(3), false);
    assert.equal(guard.refused(undefined), false);
});

test('a directory is asked about once until forget', () => {
    const { guard, asked } = guardOver({ root: win('C:/repo') });
    const before = asked.length;
    assert.equal(guard.refused(win('C:/repo/dir/x.ts')), false);
    const first = asked.length - before;
    assert.ok(first > 0);

    // A second path in the same directory costs no look at the disk.
    guard.refused(win('C:/repo/dir/y.ts'));
    assert.equal(asked.length - before, first);

    guard.forget();
    guard.refused(win('C:/repo/dir/z.ts'));
    assert.equal(asked.length - before, first * 2);
});

/** A guard over a disk that tells one case from another, as a directory can be made to. */
function guardOverExactNames(links, { listable = true } = {}) {
    const guard = createGuard({
        platform: 'win32',
        io: {
            links: directory => {
                if (!listable) throw Object.assign(new Error('EACCES'), { code: 'EACCES' });
                return new Set(Object.keys(links)
                    .filter(link => path.win32.dirname(link) === directory)
                    .map(link => lower(path.win32.basename(link))));
            },
            lstat: entry => ({ isSymbolicLink: () => entry in links }),
            readlink: entry => links[entry],
            expand: spelled => spelled,
        },
    });
    guard.addRoot(win('C:/repo'));
    return guard;
}

test('two entries that differ only in case are two questions', () => {
    const guard = guardOverExactNames({ [win('C:/repo/PKG')]: win('//evil/share/pkg') });

    // The plain one first: what was learned of it must not answer for the link.
    assert.equal(guard.refused(win('C:/repo/pkg/a.json')), false);
    assert.equal(guard.refused(win('C:/repo/PKG/b.json')), true);
    assert.equal(guard.refused(win('C:/repo/pkg/c.json')), false);
});

test('a directory that cannot be listed is asked about name by name', () => {
    const guard = guardOverExactNames({ [win('C:/repo/vendor')]: win('//evil/share/vendor') }, { listable: false });

    assert.equal(guard.refused(win('C:/repo/vendor/x.json')), true);
    assert.equal(guard.refused(win('C:/repo/src/a.ts')), false);
});

test('an entry that will not say what it is is refused', () => {
    const denied = () => { throw Object.assign(new Error('EACCES'), { code: 'EACCES' }); };
    const guard = createGuard({ platform: 'win32', io: { links: denied, lstat: denied, readlink: denied, expand: spelled => spelled } });
    guard.addRoot(win('C:/repo'));

    assert.equal(guard.refused(win('C:/repo/vendor/x.json')), true);
});

test('off Windows nothing is refused', () => {
    const never = () => assert.fail('no look is needed');
    const guard = createGuard({ platform: 'linux', io: { links: never, lstat: never, readlink: never, expand: never } });
    guard.addRoot('/home/me/repo');

    assert.equal(guard.refused('//evil/share/x.json'), false);
    assert.equal(guard.refused('/home/me/repo/src/a.ts'), false);
});

test('the worker installs the guard before it loads a compiler, and only the host names a repository', () => {
    const source = fs.readFileSync(path.join(workerDirectory, 'typescript-worker.js'), 'utf8');

    const installedAt = source.indexOf("require('./disk-guard').install()");
    assert.ok(installedAt > 0);
    for (const loaded of ["require('typescript')", "require('@vue/compiler-sfc')", "require('fs')"]) {
        assert.ok(source.indexOf(loaded) > installedAt, `${loaded} is loaded after the guard`);
    }
    const calls = [...source.matchAll(/disk\.(allow|addRoot)\(([^)]*)\)/g)].map(match => match[0]).sort();
    assert.deepEqual(calls, [
        'disk.addRoot(hostRootPath)',
        'disk.addRoot(open.rootPath)',
        'disk.allow(__dirname)',
        'disk.allow(process.execPath)',
    ]);
    // Nothing a repository names is loaded as code: every require names its module outright.
    const requires = [...source.matchAll(/\brequire\(([^)]*)\)/g)].map(match => match[1]);
    for (const argument of requires) {
        assert.match(argument, /^('[^']+'|path\.join\(__dirname, 'package\.json')$/, argument);
    }
    assert.ok(!/child_process|worker_threads|process\.binding/.test(source), 'no other way out of the process');
});

const onWindows = { skip: process.platform === 'win32' ? false : 'The installed guard refuses nothing off Windows.' };
// A name no resolver answers: were the guard to let a call through, it would fail slowly and differently.
const share = '//cc-guard-test.invalid/share/';

test('installed, every way into fs treats a share as missing', onWindows, async () => {
    install();

    assert.equal(fs.existsSync(share + 'x.json'), false);
    assert.equal(fs.statSync(share + 'x.json', { throwIfNoEntry: false }), undefined);
    assert.equal(fs.lstatSync(share + 'x.json', { throwIfNoEntry: false }), undefined);
    for (const call of [
        () => fs.statSync(share + 'x.json'),
        () => fs.readFileSync(share + 'x.json', 'utf8'),
        () => fs.readFileSync(new URL('file://cc-guard-test.invalid/share/x.json')),
        () => fs.readFileSync(Buffer.from(share + 'x.json')),
        () => fs.readdirSync(share),
        () => fs.opendirSync(share),
        () => fs.openSync(share + 'x.json', 'r'),
        () => fs.realpathSync(share + 'x.json'),
        () => fs.realpathSync.native(share + 'x.json'),
        () => fs.accessSync(share + 'x.json'),
        () => fs.createReadStream(share + 'x.json'),
        () => fs.watch(share),
        () => fs.writeFileSync(share + 'x.json', ''),
        () => fs.copyFileSync(__filename, share + 'x.json'),
        () => fs.renameSync(path.join(__dirname, 'no-such-file'), share + 'x.json'),
    ]) {
        assert.throws(call, { code: 'ENOENT' }, call.toString());
    }
    await assert.rejects(fs.promises.readFile(share + 'x.json'), { code: 'ENOENT' });
    await assert.rejects(fs.promises.stat(share + 'x.json'), { code: 'ENOENT' });
    await assert.rejects(require('node:fs/promises').readdir(share), { code: 'ENOENT' });
    const error = await new Promise(resolve => fs.readFile(share + 'x.json', resolve));
    assert.equal(error.code, 'ENOENT');
    assert.equal(await new Promise(resolve => fs.exists(share + 'x.json', resolve)), false);
    // The forms util.promisify takes are guarded as well.
    assert.equal(await require('node:util').promisify(fs.exists)(share + 'x.json'), false);
    await assert.rejects(require('node:util').promisify(fs.readFile)(share + 'x.json'), { code: 'ENOENT' });
    assert.throws(() => fs.readFileSync(new Uint8Array(Buffer.from(share + 'x.json'))), { code: 'ENOENT' });
});

test('installed, a file on a drive is still read every way', onWindows, async () => {
    install();

    const text = fs.readFileSync(__filename, 'utf8');
    assert.ok(text.includes('use strict'));
    assert.equal(fs.existsSync(__filename), true);
    assert.equal(fs.statSync(__filename).isFile(), true);
    assert.equal(fs.realpathSync.native(__filename).toLowerCase(), __filename.toLowerCase());
    assert.equal(await fs.promises.readFile(__filename, 'utf8'), text);
    assert.equal(await new Promise((resolve, reject) =>
        fs.readFile(__filename, 'utf8', (error, data) => (error ? reject(error) : resolve(data)))), text);
    const descriptor = fs.openSync(__filename, 'r');
    try {
        assert.equal(fs.fstatSync(descriptor).isFile(), true);
        assert.equal(fs.readFileSync(descriptor, 'utf8'), text);
    } finally {
        fs.closeSync(descriptor);
    }
    assert.ok(fs.readdirSync(__dirname).includes(path.basename(__filename)));
});

test('installed, a junction inside the repository is followed', onWindows, () => {
    const guard = install();
    const repository = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'cc-guard-'));
    try {
        fs.mkdirSync(path.join(repository, 'real'));
        fs.writeFileSync(path.join(repository, 'real', 'x.ts'), 'export {};\n');
        fs.symlinkSync(path.join(repository, 'real'), path.join(repository, 'linked'), 'junction');
        guard.addRoot(repository);

        assert.equal(fs.readFileSync(path.join(repository, 'linked', 'x.ts'), 'utf8'), 'export {};\n');
    } finally {
        fs.rmSync(repository, { recursive: true, force: true });
    }
});

test('installed, a symbolic link to a share inside the repository is missing', onWindows, t => {
    const guard = install();
    const repository = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'cc-guard-'));
    try {
        // Made by a process without the guard, which refuses to name the share at all.
        const made = require('node:child_process').spawnSync(process.execPath, ['-e',
            'try { require("fs").symlinkSync(process.argv[1], process.argv[2], "dir"); }'
            + ' catch (error) { process.exit(error.code === "EPERM" ? 7 : 1); }',
            win(share + 'vendor'), path.join(repository, 'vendor')]);
        if (made.status === 7) return t.skip('This account may not create symbolic links.');
        assert.equal(made.status, 0);
        guard.addRoot(repository);

        assert.equal(fs.existsSync(path.join(repository, 'vendor', 'x.ts')), false);
        assert.throws(() => fs.readFileSync(path.join(repository, 'vendor', 'x.ts')), { code: 'ENOENT' });
    } finally {
        fs.rmSync(repository, { recursive: true, force: true });
    }
});

test('off Windows installing changes nothing', { skip: process.platform === 'win32' ? 'Windows is guarded.' : false }, () => {
    const before = fs.readFileSync;
    install();

    assert.equal(fs.readFileSync, before);
    assert.equal(fs.existsSync(__filename), true);
});
