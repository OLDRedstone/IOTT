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

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((complete, fail) => { resolve = complete; reject = fail; });
  return { promise, resolve, reject };
}

function setup(count = 8) {
  const tasks = Array.from({ length: count }, (_, index) => ({
    id: `task-${index}`, name: `Task ${index}`, machineId: `machine-${index}`, deviceId: `machine-${index}`,
    status: 'pending', metricIds: [], result: '', detail: '', lastRunJson: '',
  }));
  const machines = tasks.map((task, index) => ({ id: task.machineId, host: `192.0.2.${index + 1}`, port: 502 }));
  const batches = [];
  const singles = [];
  const warnings = [];
  const errors = [];
  const api = {
    listTasks: async () => tasks.map(task => ({ ...task })),
    runTasks(ids) { const response = deferred(); batches.push({ ids: [...ids], response }); return response.promise; },
    runTask(id) { const response = deferred(); singles.push({ id, response }); return response.promise; },
  };
  const context = vm.createContext({
    computed, ref, toRaw, Error, structuredClone, useRouter: () => ({ push() {} }),
    isAxiosError: () => false,
    ElMessage: { success() {}, warning: message => warnings.push(message), error: message => errors.push(message) },
    ElMessageBox: {}, machineConnectionVerifyApi: api,
    machineConnectionDevicesApi: { list: async () => machines },
    evaluationApi: { getConfig: async () => ({}) }, businessValidationApi: { listMetrics: async () => [] },
    taskMetricOptions: () => [],
  });
  vm.runInContext(executable, context);
  const state = context.useTaskWorkbench();
  state.tasks.value = tasks.map(task => ({ ...task }));
  state.machines.value = machines;
  return { state, tasks, machines, api, batches, singles, warnings, errors };
}

const completed = task => ({ ...task, status: 'completed', result: `result:${task.id}`, lastRunJson: '{}' });

test('Seven selected tasks use one batch request, isolate failures and update out-of-order results by ID', async () => {
  const fixture = setup();
  const { state, batches, singles, tasks } = fixture;
  state.onSelectionChange(state.tasks.value.slice(0, 7));
  const running = state.executeSelectedTasks();
  assert.equal(batches.length, 1);
  assert.deepEqual(batches[0].ids, tasks.slice(0, 7).map(task => task.id));
  assert.equal(state.runningTaskCount.value, 7);
  assert.equal(state.executingIds.value.size, 7);
  await state.executeTask(state.tasks.value[7]);
  assert.equal(singles.length, 0);
  assert.match(fixture.warnings.at(-1), /最多同时执行 7 个/);
  batches[0].response.resolve(tasks.slice(0, 7).reverse().map(task => ({ taskId: task.id,
    task: task.id === 'task-2' ? null : completed(task), error: task.id === 'task-2' ? 'Target unavailable' : null })));
  await running;
  assert.equal(state.runningTaskCount.value, 0);
  assert.equal(state.executingIds.value.size, 0);
  for (const task of state.tasks.value.slice(0, 7)) {
    assert.equal(task.status, task.id === 'task-2' ? 'pending' : 'completed');
    if (task.id !== 'task-2') assert.equal(task.result, `result:${task.id}`);
  }
  assert.match(fixture.errors[0], /Task 2.*Target unavailable/);
});

test('Tasks already running on the server count toward the seven-task limit without double counting local IDs', async () => {
  const { state, batches, warnings, tasks } = setup();
  state.tasks.value.slice(0, 6).forEach(task => { task.status = 'running'; });
  state.executingIds.value.add('task-0');
  state.onSelectionChange(state.tasks.value.slice(6));
  await state.executeSelectedTasks();
  assert.equal(batches.length, 0);
  assert.match(warnings[0], /当前运行 6 个/);
  state.onSelectionChange([state.tasks.value[6]]);
  const running = state.executeSelectedTasks();
  assert.equal(state.runningTaskCount.value, 7);
  batches[0].response.resolve([{ taskId: tasks[6].id, task: completed(tasks[6]), error: null }]);
  await running;
  assert.equal(state.runningTaskCount.value, 6);
});

for (const sharedKey of ['machineId', 'endpoint']) {
  test(`A selected batch rejects duplicate targets identified by ${sharedKey}`, async () => {
    const { state, batches, warnings } = setup(2);
    if (sharedKey === 'machineId') state.tasks.value[1].machineId = state.tasks.value[0].machineId;
    else state.machines.value[1].host = state.machines.value[0].host;
    state.onSelectionChange(state.tasks.value);
    await state.executeSelectedTasks();
    assert.equal(batches.length, 0);
    assert.equal(state.executingIds.value.size, 0);
    assert.match(warnings[0], /同一目标/);
  });
}

test('Independent single-row runs preserve per-ID loading and finished results when an earlier refresh returns late', async () => {
  const { state, api, singles, tasks } = setup(2);
  const first = state.executeTask(state.tasks.value[0]);
  const second = state.executeTask(state.tasks.value[1]);
  assert.equal(singles.length, 2);
  const refresh = deferred();
  api.listTasks = () => refresh.promise;
  const loading = state.loadPage();
  singles[1].response.resolve(completed(tasks[1]));
  await second;
  assert.equal(state.executingIds.value.has('task-0'), true);
  assert.equal(state.executingIds.value.has('task-1'), false);
  refresh.resolve(tasks.map(task => ({ ...task, status: 'running' })));
  await loading;
  assert.equal(state.tasks.value[1].status, 'completed');
  assert.equal(state.tasks.value[1].result, 'result:task-1');
  singles[0].response.resolve(completed(tasks[0]));
  await first;
  assert.equal(state.runningTaskCount.value, 0);
});

test('A second task on an active target is rejected while unrelated targets remain available', async () => {
  const { state, singles, tasks, warnings } = setup(3);
  state.tasks.value[1].machineId = state.tasks.value[0].machineId;
  const first = state.executeTask(state.tasks.value[0]);
  await state.executeTask(state.tasks.value[1]);
  assert.equal(singles.length, 1);
  assert.match(warnings[0], /同一目标/);
  const third = state.executeTask(state.tasks.value[2]);
  assert.equal(singles.length, 2);
  singles[0].response.resolve(completed(tasks[0]));
  singles[1].response.resolve(completed(tasks[2]));
  await Promise.all([first, third]);
});

test('Batch request and reconciliation failure restore original statuses and release every local slot', async () => {
  const { state, api, batches, errors } = setup(2);
  state.tasks.value[0].status = 'completed';
  state.tasks.value[0].result = 'previous result';
  state.tasks.value[0].completedAt = '2026-09-28T10:00:00Z';
  state.onSelectionChange(state.tasks.value);
  const running = state.executeSelectedTasks();
  assert.equal(state.tasks.value[0].completedAt, null);
  api.listTasks = async () => { throw new Error('Refresh unavailable'); };
  batches[0].response.reject(new Error('Network unavailable'));
  await running;
  assert.equal(state.executingIds.value.size, 0);
  assert.equal(state.runningTaskCount.value, 0);
  assert.equal(state.tasks.value[0].status, 'completed');
  assert.equal(state.tasks.value[0].result, 'previous result');
  assert.equal(state.tasks.value[0].completedAt, '2026-09-28T10:00:00Z');
  assert.equal(state.tasks.value[1].status, 'pending');
  assert.match(errors[0], /Network unavailable/);
});

test('A completed batch row can restart while failure reconciliation waits without losing the new run loading', async () => {
  const { state, api, batches, singles, tasks } = setup(2);
  const refresh = deferred();
  const refreshStarted = deferred();
  api.listTasks = () => { refreshStarted.resolve(); return refresh.promise; };
  state.onSelectionChange(state.tasks.value);
  const batch = state.executeSelectedTasks();
  batches[0].response.resolve([
    { taskId: tasks[0].id, task: completed(tasks[0]), error: null },
    { taskId: tasks[1].id, task: null, error: 'Target unavailable' },
  ]);
  await refreshStarted.promise;
  assert.equal(state.executingIds.value.has(tasks[0].id), false);
  const restarted = state.executeTask(state.tasks.value[0]);
  assert.equal(singles.length, 1);
  refresh.resolve(tasks);
  await batch;
  assert.equal(state.executingIds.value.has(tasks[0].id), true);
  assert.equal(state.runningTaskCount.value, 1);
  assert.equal(state.tasks.value[0].status, 'running');
  singles[0].response.resolve(completed(tasks[0]));
  await restarted;
  assert.equal(state.executingIds.value.size, 0);
});
