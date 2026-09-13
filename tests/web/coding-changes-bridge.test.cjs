const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

test('native coding changes snapshots pass through the versioned inbound bridge', () => {
  let receive;
  const delivered = [];
  const context = vm.createContext({
    chrome: { webview: { addEventListener: (name, handler) => { receive = handler; }, postMessage() {} } },
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    dispatchEvent: event => delivered.push(event),
  });
  vm.runInContext(fs.readFileSync(path.resolve(__dirname, '../../src/GoWinUI.App/Assets/Web/bridge.js'), 'utf8'), context);
  const payload = { sessionId:'session',messageId:'answer',workspacePath:'C:/project',revision:2,
    files:[{path:'src/app.py',addedLines:4,removedLines:1,diff:'@@ -1 +1 @@\n-before\n+after'}] };
  receive({data:{version:1,type:'coding.changes',payload}});
  assert.equal(delivered.length, 1);
  assert.equal(delivered[0].type, 'go:host-message');
  assert.equal(delivered[0].detail.payload, payload);
  receive({data:{version:2,type:'coding.changes',payload}});
  receive({data:{version:1,type:'coding.changes',payload:'invalid'}});
  assert.equal(delivered.length, 1, 'wrong protocol versions and non-object snapshots are ignored');
});
