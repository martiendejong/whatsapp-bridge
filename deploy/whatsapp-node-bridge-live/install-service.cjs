const { Service } = require('node-windows');
const path = require('path');

const svc = new Service({
    name: 'WhatsAppBridgeNode',
    description: 'WhatsApp Web.js Node.js bridge for Bugatti Insights',
    script: path.join(__dirname, 'service-wrapper.cjs'),
});

if (process.argv[2] === 'uninstall') {
    svc.on('uninstall', () => { console.log('Uninstalled'); process.exit(0); });
    svc.uninstall();
} else {
    svc.on('install', () => { console.log('Installed - starting...'); svc.start(); });
    svc.on('alreadyinstalled', () => { console.log('Already installed'); process.exit(0); });
    svc.on('error', (e) => { console.error('Error:', e); process.exit(1); });
    svc.install();
}
