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

/** A guard on Windows over a made-up disk: `links` maps a path to where it leads. */
function windowsGuard(links = {}) {
    const lowered = new Map(Object.entries(links).map(([from, to]) => [win(from).toLowerCase(), win(to)]));
    const looked = [];
    const guard = createGuard({
        platform: 'win32',
        io: {
            links: directory => {
                looked.push(directory);
                return new Set([...lowered.keys()]
                    .filter(link => path.win32.dirname(link) === directory.toLowerCase())
                    .map(link => path.win32.basename(link)));
            },
            lstat: entry => {
                looked.push(entry);
                return lowered.has(entry.toLowerCase()) ? { isSymbolicLink: () => true } : undefined;
            },
            readlink: entry => lowered.get(entry.toLowerCase()),
        },
    });
    guard.looked = looked;
    return guard;
}

test('a drive path is read and every spelling of a share is refused', () => {
    const guard = windowsGuard();
    guard.addRoot(win('C:/repo'));

    assert.equal(guard.refused(win('C:/repo/src/a.ts')), false);
    assert.equal(guard.refused('C:/repo/src/a.ts'), false);
    for (const spelling of [
        win('//evil/share/x.json'),
        '//evil/share/x.json',
        win('/') + '/evil/share/x.json',
        win('//evil/share'),
        win('//evil/share/a/../b'),
        win('//?/UNC/evil/share/x.json'),
        win('//./UNC/evil/share/x.json'),
        win('//?/C:/repo/src/a.ts'),
        win('//evil@80/share/x.json'),
        win('//evil@SSL/DavWWWRoot/x.json'),
        win('//127.0.0.1/C$/repo/src/a.ts'),
    ]) {
        assert.equal(guard.refused(spelling), true, spelling);
    }
});

test('a share is refused when it is named by a URL or by bytes', () => {
    const guard = windowsGuard();

    assert.equal(guard.refused(new URL('file://evil/share/x.json')), true);
    assert.equal(guard.refused(Buffer.from(win('//evil/share/x.json'))), true);
    assert.equal(guard.refused(new URL('file:///C:/repo/a.ts')), false);
    assert.equal(guard.refused(Buffer.from(win('C:/repo/a.ts'))), false);
});

test('what cannot name a file is refused, and what is no path is left alone', () => {
    const guard = windowsGuard();

    assert.equal(guard.refused(''), true);
    assert.equal(guard.refused('C:/a\0b'), true);
    assert.equal(guard.refused(3), false);
    assert.equal(guard.refused(undefined), false);
});

test('the share of the repository and of a place the owner chose are read, and no other', () => {
    const guard = windowsGuard();
    guard.addRoot(win('//files/team/repo'));
    guard.allow(win('//tools/node/bin/node.exe'));

    assert.equal(guard.refused(win('//files/team/repo/src/a.ts')), false);
    assert.equal(guard.refused(win('//FILES/Team/other/tsconfig.json')), false);
    assert.equal(guard.refused(win('//tools/node/lib')), false);
    assert.equal(guard.refused(win('//files/other/x')), true);
    assert.equal(guard.refused(win('//evil/team/repo')), true);
});

test('a link inside the repository that leads to a share is refused, wherever it sits in the path', () => {
    const guard = windowsGuard(
        { 'C:/repo/vendor': '//evil/share/vendor', 'C:/repo/src/leaf.ts': '//evil/share/leaf.ts' });
    guard.addRoot(win('C:/repo'));

    assert.equal(guard.refused(win('C:/repo/vendor/lib/x.ts')), true);
    assert.equal(guard.refused(win('C:/repo/vendor')), true);
    assert.equal(guard.refused(win('C:/repo/src/leaf.ts')), true);
    assert.equal(guard.refused(win('C:/repo/src/missing.ts')), false);
    // Nothing on the share was looked at to decide.
    assert.deepEqual(guard.looked.filter(entry => entry.startsWith(B + B)), []);
});

test('a link to a drive is followed, as a package manager\'s links are', () => {
    const guard = windowsGuard(
        {
            'C:/repo/node_modules/pkg': 'C:/repo/node_modules/.pnpm/pkg@1/node_modules/pkg',
            'C:/repo/shared': 'D:/elsewhere/shared',
            'C:/repo/relative': '../sibling',
        });
    guard.addRoot(win('C:/repo'));

    assert.equal(guard.refused(win('C:/repo/node_modules/pkg/index.d.ts')), false);
    assert.equal(guard.refused(win('C:/repo/shared/x.ts')), false);
    assert.equal(guard.refused(win('C:/repo/relative/x.ts')), false);
});

test('a link that leads through another link to a share is refused, and a loop of links ends', () => {
    const guard = windowsGuard({
        'C:/repo/a': 'C:/repo/b',
        'C:/repo/b': '//evil/share/b',
        'C:/repo/loop1': 'C:/repo/loop2',
        'C:/repo/loop2': 'C:/repo/loop1',
    });
    guard.addRoot(win('C:/repo'));

    assert.equal(guard.refused(win('C:/repo/a/x.ts')), true);
    assert.equal(guard.refused(win('C:/repo/loop1/x.ts')), true);
});

test('links are looked at again after forget', () => {
    const guard = windowsGuard();
    guard.addRoot(win('C:/repo'));
    assert.equal(guard.refused(win('C:/repo/dir/x.ts')), false);
    const first = guard.looked.length;
    assert.ok(first > 0);

    // A second path in the same directory costs no look at the disk.
    guard.refused(win('C:/repo/dir/y.ts'));
    assert.equal(guard.looked.length, first);

    guard.forget();
    guard.refused(win('C:/repo/dir/z.ts'));
    assert.equal(guard.looked.length, first * 2);
});

test('off Windows nothing is refused', () => {
    const guard = createGuard({ platform: 'linux', io: { lstat: () => assert.fail('no look is needed'), readlink: () => '' } });
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
