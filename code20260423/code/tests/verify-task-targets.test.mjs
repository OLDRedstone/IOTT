import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { computed, ref, toRaw } from 'vue';

const source = readFileSync(new URL('../src/views/verify/evaluation/taskState.ts', import.meta.url), 'utf8');
const executable = ts.transpileModule(source.replace(/^import .*;\r?$/gm, '')
  .replace('export function useTaskWorkbench', 'function useTaskWorkbench')
  + '\nglobalThis.useTaskWorkbench = useTaskWorkbench;', {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;
const device = (id, port, extra = {}) => ({ id, type: 'CNC', host: '127.0.0.1', port, extendedProperties: {}, ...extra });
const task = (id, deviceId) => ({ id, name: id, machineId: deviceId, deviceId, status: 'pending', metricIds: [] });

function fixture(devices, tasks) {
  const warnings = [];
  const requests = [];
  const listArguments = [];
  const context = vm.createContext({
    computed, ref, toRaw, Error, structuredClone, useRouter: () => ({ push() {} }), isAxiosError: () => false,
    ElMessage: { success() {}, error() {}, warning: message => warnings.push(message) }, ElMessageBox: {},
    machineConnectionVerifyApi: {
      listTasks: async () => tasks,
      startTasks: async ids => { requests.push(ids); return ids.map(id => ({ taskId: id, task: { ...tasks.find(row => row.id === id), status: 'completed' } })); },
    },
    machineConnectionDevicesApi: { list: async (...args) => { listArguments.push(args); return devices; } },
    evaluationApi: { getConfig: async () => ({}) }, businessValidationApi: { listMetrics: async () => [] },
    taskMetricOptions: () => [],
  });
  vm.runInContext(executable, context);
  const state = context.useTaskWorkbench();
  state.tasks.value = tasks;
  state.machines.value = devices;
  async function run() { state.onSelectionChange(state.tasks.value); await state.executeSelectedTasks(); }
  return { state, warnings, requests, listArguments, run };
}

test('Linked file device and aliases of its actual endpoint conflict across tasks', async () => {
  for (const second of ['file', 'alias']) {
    const f = fixture([device('main', 3201, { extendedProperties: { transferDeviceId: 'file' } }), device('file', 3202), device('alias', 3202)],
      [task('main-task', 'main'), task('other-task', second)]);
    await f.run();
    assert.equal(f.requests.length, 0);
    assert.match(f.warnings[0], /同一目标/);
  }
});

test('An inline transfer wins over an unused missing association', async () => {
  const f = fixture([device('main', 3301, { transfer: { host: '127.0.0.1', port: 3321 }, extendedProperties: { transferDeviceId: 'missing' } })], [task('main-task', 'main')]);
  await f.run();
  assert.equal(f.requests.length, 1);
  assert.equal(f.warnings.length, 0);
});

test('Linked rows use their primary endpoint rather than their unused inline transfer', async () => {
  const f = fixture([device('main', 3701, { extendedProperties: { transferDeviceId: 'file' } }),
    device('file', 3702, { transfer: { host: '127.0.0.1', port: 3703 } }), device('unrelated', 3703)],
    [task('main-task', 'main'), task('other-task', 'unrelated')]);
  await f.run();
  assert.equal(f.requests.length, 1);
  assert.equal(f.warnings.length, 0);
});

for (const kind of ['missing', 'ambiguous', 'cycle', 'self']) {
  test(`Preflight rejects ${kind} transfer associations without starting anything`, async () => {
    const main = device('main', 3401, { extendedProperties: { transferDeviceId: kind === 'self' ? 'main' : 'file' } });
    const file = device('file', 3402, { extendedProperties: kind === 'cycle' ? { transferDeviceId: 'main' } : {} });
    const devices = kind === 'missing' || kind === 'self' ? [main] : kind === 'ambiguous' ? [main, file, { ...file }] : [main, file];
    const f = fixture(devices, [task('main-task', 'main')]);
    await f.run();
    assert.equal(f.requests.length, 0);
    assert.match(f.warnings[0], /不存在|不唯一|循环/);
  });
}

test('A concurrent peer association also reserves its transfer device', async () => {
  const f = fixture([device('main', 3501), device('peer', 3502, { extendedProperties: { transferDeviceId: 'file' } }), device('file', 3503)],
    [{ ...task('group', 'main'), options: { concurrentDeviceIds: ['peer'] } }, task('other', 'file')]);
  await f.run();
  assert.equal(f.requests.length, 0);
  assert.match(f.warnings[0], /同一目标/);
});

test('All device types are loaded for links without adding them to the CNC picker', async () => {
  const f = fixture([device('main', 3601, { extendedProperties: { transferDeviceId: 'file' } }), device('file', 3602, { type: 'PLC' })], [task('main-task', 'main')]);
  await f.state.loadPage();
  assert.equal(f.listArguments[0].length, 0);
  assert.deepEqual(Array.from(f.state.machines.value, row => row.id), ['main']);
  await f.run();
  assert.equal(f.requests.length, 1);
  assert.equal(f.warnings.length, 0);
});
