// CommonJS wrapper for ES module index.js
// node-windows requires CommonJS, so we spawn node as a child process

const { spawn, execFile } = require('child_process');
const path = require('path');

const child = spawn(process.execPath, [path.join(__dirname, 'index.js')], {
    cwd: __dirname,
    env: Object.assign({}, process.env, { PORT: '3000' }),
    stdio: 'inherit'
});

child.on('exit', (code) => {
    process.exit(code || 0);
});

// Windows has no real POSIX signals: child.kill('SIGTERM'/'SIGINT') only ever
// terminates the direct child process, never the process tree. index.js
// spawns Puppeteer, which spawns its own Chromium subprocess — killing only
// the Node child leaves Chromium running, holding the profile lock and
// sometimes the port, so the next service start fails with EADDRINUSE.
// taskkill /t kills the whole tree in one shot.
function killTree() {
    execFile('taskkill', ['/pid', child.pid, '/t', '/f'], () => {
        process.exit(0);
    });
}

process.on('SIGTERM', killTree);
process.on('SIGINT', killTree);
