import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';

function load(path, context = {}) {
  const source = readFileSync(new URL(path, import.meta.url), 'utf8')
    .replace(/^import[^\n]*$/gm, '').replace(/^export /gm, '')
    .replaceAll('import.meta.env.VITE_MACHINE_CONNECTION_API', 'undefined');
  const code = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
  const sandbox = vm.createContext(context);
  vm.runInContext(code, sandbox);
  return sandbox;
}
function rules() { return load('../src/views/verify/evaluation/automationRules.ts'); }
function item(context, metricId = 'communication-stability') {
  return { metricId, automation: context.defaultFor(metricId), protocols: ['ModbusTCP'], desc: '旧文本：246%', scoring: { excellent: '旧规则不执行' } };
}

test('Exactly seven independent rules; only the documented 100-device requirement supplies a default threshold', () => {
  const context = rules();
  const units = { 'industrial-protocol': '%', 'communication-stability': 'min', 'max-connections': 'count',
    'transfer-protocol': 'count', 'file-integrity': '%', 'transfer-speed': 'MB/s', 'file-size': 'MB' };
  for (const [metricId, unit] of Object.entries(units)) {
    const value = item(context, metricId);
    assert.equal(value.automation.unit, unit);
    assert.deepEqual(JSON.parse(JSON.stringify(value.automation.passRule)), metricId === 'max-connections'
      ? { comparison: 'gte', threshold: 100, unit: 'count' } : null);
    assert.equal(value.automation.test.readDataType, 'String');
    assert.equal(value.automation.test.targetDirectory, '/');
    assert.equal(context.validate(value), undefined);
    value.automation.test.readAddress = 'mutated';
    assert.equal(context.defaultFor(metricId).test.readAddress, '');
  }
  for (const id of ['ordinary', '', '__proto__', 'toString', undefined]) assert.equal(context.defaultFor(id), null);
});

test('Concurrency accepts 100, rejects 101 and defaults to the documented acceptance line', () => {
  const context = rules();
  const value = item(context, 'max-connections');
  assert.equal(value.automation.test.maxConnections, 100);
  assert.deepEqual(JSON.parse(JSON.stringify(value.automation.scoreBands)), [{ min: 100, score: 100 }, { min: 0, score: 0 }]);
  assert.match(context.thresholdSummary(value), /≥ 100 count/);
  for (const maximum of [1, 64, 65, 100]) {
    value.automation.test.maxConnections = maximum;
    assert.equal(context.validate(value), undefined);
  }
  value.automation.test.maxConnections = 101;
  assert.equal(typeof context.validate(value), 'string');
});

test('Rules reject invalid units, implied percentages above 100, malformed criteria and unsafe settings', () => {
  const context = rules();
  for (const change of [
    rule => { rule.passRule = { comparison: 'gte', threshold: 99, unit: '%' }; },
    rule => { rule.scoreBands = [{ min: 0, score: 0 }, { min: 0, score: 100 }]; },
    rule => { rule.scoreBands = [{ min: 0, score: 101 }]; },
    rule => { rule.test.durationMinutes = NaN; },
    rule => { rule.test.readAddress = 'bad\naddress'; },
    rule => { rule.test.readDataType = 'Number'; },
    rule => { rule.test.targetDirectory = '/test/../programs'; },
  ]) {
    const value = item(context);
    change(value.automation);
    assert.equal(typeof context.validate(value), 'string');
  }
  const percentage = item(context, 'industrial-protocol');
  percentage.automation.passRule = { comparison: 'gte', threshold: 246, unit: '%' };
  assert.equal(typeof context.validate(percentage), 'string');
  assert.equal(context.validate({ metricId: 'industrial-protocol', automation: null }), undefined);
});

test('Unit changes convert scoring bands but preserve the independent criterion and legitimate zero', () => {
  const context = rules();
  const value = item(context, 'transfer-speed');
  value.automation.passRule = { comparison: 'gte', threshold: 5, unit: 'MB/s' };
  context.changeUnit(value.automation, 'KiB/s');
  assert.equal(value.automation.unit, 'KiB/s');
  assert.equal(value.automation.scoreBands[1].min, 5120);
  assert.equal(value.automation.scoreBands.at(-1).score, 0);
  assert.equal(value.automation.passRule.unit, 'MB/s');
  assert.equal(context.validate(value), undefined);
  assert.equal(context.convert(1024, 'KiB', 'MiB'), 1);
  assert.equal(context.convert(1, 'MB', 'MB/s'), null);
});

test('Independent connections and legacy sessions remain configurable without enabling implicit-all targets', () => {
  const context = rules();
  const value = item(context, 'max-connections');
  for (const mode of ['devices', 'connections', 'sessions']) {
    value.automation.test.concurrencyMode = mode;
    assert.equal(context.validate(value), undefined);
  }
  value.automation.test.concurrencyMode = 'all';
  assert.equal(typeof context.validate(value), 'string');
});

test('Protocol aliases and live summaries use the structured rule, not old explanatory text', () => {
  const context = rules();
  assert.equal(context.normalizeProtocol('OPC/OPC UA'), 'OPC UA');
  assert.equal(context.normalizeProtocol('Modbus TCP'), 'Modbus');
  assert.equal(context.normalizeProtocol('Fanuc Focas'), 'FOCAS');
  for (const [protocol, normalized] of [['OPC', 'OPC'], ['NC-Link API', 'NC-Link'], ['GskWebServer', 'GSK'],
    ['Gskrm', 'GSK'], ['Profibus DP', 'Profibus'], ['GskrmFileTransfer', 'GSKRMFILETRANSFER'], ['SFTP', 'SFTP']]) {
    assert.equal(context.normalizeProtocol(protocol), normalized);
  }
  const value = item(context);
  value.automation.test.readAddress = 'fixture.address';
  value.automation.test.readDataType = 'UInt16';
  const summary = context.ruleSummary(value);
  assert.ok(summary.includes('fixture.address') && summary.includes('UInt16') && summary.includes('未配置'));
  assert.ok(!summary.includes('246'));
  const file = item(context, 'transfer-speed');
  file.automation.test.targetDirectory = '/isolated-test';
  assert.ok(context.ruleSummary(file).includes('/isolated-test'));
});

function apiFixture() {
  const calls = [];
  const client = Object.fromEntries(['get', 'put', 'post'].map(method => [method, async (...args) => {
    calls.push({ method, args }); return { data: { id: 'fixture' } };
  }]));
  class FormDataFake { append(name, value) { this[name] = value; } }
  const context = load('../src/api/evaluation.ts', { createMachineConnectionClient: () => client, FormData: FormDataFake });
  return { calls, api: vm.runInContext('evaluationApi', context) };
}

test('Attachment API accepts exactly 200 MiB and forwards a dedicated 30-minute deadline and AbortSignal', async () => {
  const { api, calls } = apiFixture();
  const signal = new AbortController().signal;
  const file = { name: 'fixture.nc', size: 200 * 1024 * 1024 };
  await api.uploadAttachment(file, signal);
  assert.equal(calls[0].args[1].file, file);
  assert.equal(calls[0].args[2].timeout, 30 * 60_000);
  assert.equal(calls[0].args[2].signal, signal);
  await api.downloadAttachment('a/b', signal);
  assert.ok(calls[1].args[0].endsWith('a%2Fb'));
  assert.equal(calls[1].args[1].timeout, 30 * 60_000);
  assert.equal(calls[1].args[1].signal, signal);
  for (const size of [0, 200 * 1024 * 1024 + 1]) await assert.rejects(api.uploadAttachment({ ...file, size }), /200 MiB/);
  assert.equal(calls.length, 2);
});

test('Category templates and saving automation forward the actual selected configuration', async () => {
  const { api, calls } = apiFixture();
  await api.downloadTemplate('machining');
  assert.equal(calls[0].args[1].params.category, 'machining');
  const config = { category: 'machine', indicators: [{ automation: rules().defaultFor('industrial-protocol') }] };
  await api.saveConfig(config);
  assert.equal(calls[1].args[1], config);
  assert.equal(config.indicators[0].automation.passRule, null);
});

test('Task preview preserves two-decimal run means while weighted knowledge totals retain their own format', () => {
  const context = load('../src/views/verify/evaluation/knowledgeModel.ts');
  assert.equal(context.formatScore(93.33, 2), '93.33');
  assert.equal(context.formatScore(93.33), '93.3');
  assert.equal(context.formatScore(0, 2), '0.00');
  assert.equal(context.formatScore(null, 2), '待评分');
  assert.equal(context.formatScore(NaN, 2), '待评分');
});

test('Structured rule editor script and template compile', () => {
  const filename = new URL('../src/views/verify/evaluation/automationRuleEditor.vue', import.meta.url).pathname;
  const source = readFileSync(new URL('../src/views/verify/evaluation/automationRuleEditor.vue', import.meta.url), 'utf8');
  const { descriptor, errors } = parse(source, { filename });
  assert.deepEqual(errors, []);
  const script = compileScript(descriptor, { id: 'automation-rule-integration' });
  const template = compileTemplate({ source: descriptor.template.content, filename, id: 'automation-rule-integration',
    compilerOptions: { bindingMetadata: script.bindings } });
  assert.deepEqual(template.errors, []);
});
