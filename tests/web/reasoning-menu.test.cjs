const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { TestNode } = require('./test-dom.cjs');
const file = '../../src/GoWinUI.App/Assets/Web/reasoning-menu.js';
const { options } = require(file);

test('unknown metadata keeps Auto; safe future levels remain selectable', () => {
  assert.deepEqual(options({}).map(x => x.value), ['auto']);
  assert.deepEqual(options({levels:['low','future_level','low','<script>']}).map(x => x.value), ['auto','low','future_level']);
});

test('model switches reject stale responses and manual choices wait for host confirmation', async () => {
  const nodes = new Map();
  const node = id => { if (!nodes.has(id)) nodes.set(id, new TestNode('button')); return nodes.get(id); };
  const document = {getElementById:node, addEventListener(){}, createElement:tag => {
    const item = new TestNode(tag); item.focus=()=>{}; return item;
  }};
  node('reasoning-button').focus=()=>{};
  node('reasoning-menu').hidden=true;
  const sent=[]; let receive;
  const context={document, goBridge:{post:(type,payload)=>{const id=String(sent.length+1);sent.push({type,payload,id});return id;}}, addEventListener:(type,fn)=>{receive=fn;}};
  vm.runInNewContext(fs.readFileSync(require.resolve(file),'utf8'),context);
  const emit=(type,payload,requestId)=>receive({detail:{type,payload,requestId}});
  emit('state.snapshot',{reasoningModelId:'A',reasoningRole:'coding'});
  emit('state.snapshot',{reasoningModelId:'B',reasoningRole:'coding'});
  emit('reasoning.snapshot',{modelId:'A',role:'coding',selected:'high',levels:['high'],available:true},'1');
  assert.equal(node('reasoning-label').textContent,'Reasoning');
  emit('reasoning.snapshot',{modelId:'B',role:'coding',selected:'auto',levels:['low','high'],available:true},'2');
  await node('reasoning-options').children[1].dispatch('click');
  assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1).payload)),{modelId:'B',role:'coding',effort:'low'});
  assert.equal(node('reasoning-label').textContent,'Reasoning');
  emit('reasoning.snapshot',{modelId:'B',role:'coding',selected:'low',levels:['low','high'],available:true},'3');
  assert.equal(node('reasoning-label').textContent,'Reasoning');
  assert.equal(node('reasoning-button').title,'Reasoning: Niedrig');
  emit('chat.started',{});
  assert.equal(node('reasoning-button').disabled,true);
  emit('chat.completed',{});
  assert.equal(node('reasoning-button').disabled,false);
});
