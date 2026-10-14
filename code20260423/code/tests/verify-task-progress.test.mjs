import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { computed, ref, toRaw } from 'vue';

function compile(path, expose) {
  const source = readFileSync(new URL(path, import.meta.url), 'utf8');
  return ts.transpileModule(source.replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '')
    + `\nglobalThis.${expose} = ${expose};`, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } }).outputText;
}
const modelCode = compile('../src/views/verify/evaluation/taskModel.ts', 'taskRun');
const stateCode = compile('../src/views/verify/evaluation/taskState.ts', 'useTaskWorkbench');
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function snapshot(runId = 'active', progressPercent = 25) {
  return { runId, status: 'running', metrics: [{ metricId: 'communication-stability', executionStatus: 'running', progressPercent, score: null }], progressPercent };
}
function setup(count = 2) {
  let rows = Array.from({ length: count }, (_, index) => ({ id: `task-${index}`, name: `Task ${index}`,
    deviceId: `device-${index}`, machineId: `device-${index}`, status: 'pending', metricIds: ['communication-stability'],
    createdAt: '', params: '', detail: '', result: '', lastRunJson: JSON.stringify({ runId: 'old', status: 'completed', metrics: [], totalScore: 90 }) }));
  const machines = rows.map((row, index) => ({ id: row.deviceId, host: `192.0.2.${index + 1}`, port: 502 }));
  const timers = new Map();
  let timerId = 0, cleanup;
  const starts = [], messages = [];
  const api = {
    listTasks: async () => structuredClone(rows),
    startTask: async id => {
      starts.push(id);
      const index = rows.findIndex(row => row.id === id);
      rows[index] = { ...rows[index], status: 'running', activeRunId: 'active', currentRunJson: JSON.stringify(snapshot('active', 0)) };
      return structuredClone(rows[index]);
    },
    startTasks: async ids => Promise.all(ids.map(async id => ({ taskId: id, task: await api.startTask(id), error: null }))),
    runTask: () => { throw new Error('Should not use synchronous run when background start exists'); },
  };
  const context = vm.createContext({ computed, ref, toRaw, structuredClone, AbortController,
    onScopeDispose: callback => { cleanup = callback; }, useRouter: () => ({ push() {} }), isAxiosError: () => false,
    ElMessage: { success: value => messages.push(value), warning: value => messages.push(value), error: value => messages.push(value) }, ElMessageBox: {},
    setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; }, clearTimeout: id => timers.delete(id),
    machineConnectionVerifyApi: api, machineConnectionDevicesApi: { list: async () => machines },
    evaluationApi: { getConfig: async () => ({ indicators: [] }) }, businessValidationApi: { listMetrics: async () => [] },
  });
  vm.runInContext(modelCode, context);
  vm.runInContext(stateCode, context);
  const state = context.useTaskWorkbench();
  state.tasks.value = structuredClone(rows);
  state.machines.value = machines;
  return { state, context, api, starts, timers, messages, machines, get rows() { return rows; }, set rows(value) { rows = value; }, dispose: () => cleanup() };
}

test('Running display never substitutes old history, mismatched active IDs are ignored, zero remains scored', () => {
  const { context, rows } = setup(1);
  const task = { ...rows[0], status: 'running', activeRunId: 'active' };
  assert.equal(context.taskRun(task), null);
  task.currentRunJson = JSON.stringify(snapshot('wrong-run'));
  assert.equal(context.taskRun(task), null);
  task.currentRunJson = JSON.stringify(snapshot());
  assert.equal(context.taskRun(task).progressPercent, 25);
  assert.equal(context.taskRun(task, 'history').runId, 'old');
  assert.equal(vm.runInContext('taskScore(0)', context), '0 分');
  assert.equal(vm.runInContext('taskScore(null)', context), '待评分');
  assert.equal(vm.runInContext('newTask([]).options.allowFileWrites', context), false);
});

test('Background acceptance releases local HTTP loading while server progress keeps the slot occupied', async () => {
  const fixture = setup(1);
  const { state } = fixture;
  const oldHistory = state.tasks.value[0].lastRunJson;
  await state.executeTask(state.tasks.value[0]);
  assert.equal(state.executingIds.value.size, 0);
  assert.equal(state.runningTaskCount.value, 1);
  assert.equal(state.tasks.value[0].lastRunJson, oldHistory);
  assert.equal([...fixture.timers.values()][0].delay, 2000);
  state.viewResult(state.tasks.value[0]);
  fixture.rows[0].currentRunJson = JSON.stringify(snapshot('active', 62.5));
  await state.refreshTasks();
  assert.equal(fixture.context.taskRun(state.selectedTask.value).progressPercent, 62.5);
  fixture.rows[0] = { ...fixture.rows[0], status: 'completed', activeRunId: null, currentRunJson: '', lastRunJson: JSON.stringify({ ...snapshot('active', 100), status: 'completed', completedAt: '2026-09-29', totalScore: 0 }) };
  await state.refreshTasks();
  assert.equal(state.runningTaskCount.value, 0);
  assert.equal(state.selectedTask.value.status, 'completed');
  assert.equal(fixture.context.taskRun(state.selectedTask.value).totalScore, 0);
  fixture.dispose();
  assert.equal(fixture.timers.size, 0);
});

test('A slow pre-start poll cannot overwrite newly accepted run state', async () => {
  const { state, api, rows, context, dispose } = setup(1);
  const poll = deferred();
  const originalRows = structuredClone(rows);
  api.listTasks = () => poll.promise;
  const refresh = state.refreshTasks();
  await state.executeTask(state.tasks.value[0]);
  poll.resolve(originalRows);
  await refresh;
  assert.equal(state.tasks.value[0].status, 'running');
  assert.equal(context.taskRun(state.tasks.value[0]).runId, 'active');
  dispose();
});

test('Uncertain start is reconciled through polling without retrying the write or fabricating failure', async () => {
  const fixture = setup(1);
  const { state, api } = fixture;
  api.startTask = async () => { throw new Error('Network unavailable'); };
  api.listTasks = async () => { throw new Error('Reconciliation unavailable'); };
  await state.executeTask(state.tasks.value[0]);
  assert.equal(state.tasks.value[0].status, 'pending');
  assert.equal(state.executingIds.value.size, 0);
  assert.equal(fixture.timers.size, 1);
  api.listTasks = async () => [{ ...fixture.rows[0], status: 'running', activeRunId: 'late-accept', currentRunJson: JSON.stringify(snapshot('late-accept', 30)) }];
  await state.refreshTasks();
  assert.equal(state.tasks.value[0].status, 'running');
  assert.equal(fixture.context.taskRun(state.tasks.value[0]).progressPercent, 30);
  fixture.dispose();
});

for (const targetType of ['concurrent-group', 'transfer-endpoint']) {
  test(`Preflight reservations include ${targetType}`, async () => {
    const fixture = setup(2);
    if (targetType === 'concurrent-group') fixture.state.tasks.value[0].options = { concurrentDeviceIds: ['device-1'] };
    else fixture.machines[0].transfer = { host: fixture.machines[1].host, port: 502, protocol: 'fake' };
    fixture.state.onSelectionChange(fixture.state.tasks.value);
    await fixture.state.executeSelectedTasks();
    assert.equal(fixture.starts.length, 0);
    assert.match(fixture.messages.at(-1), /同一目标/);
    fixture.dispose();
  });
}

test('Polling aborts on dispose and ignores an already pending response', async () => {
  const fixture = setup(1);
  const poll = deferred();
  let signal;
  fixture.api.listTasks = received => { signal = received; return poll.promise; };
  fixture.state.startPolling();
  const refresh = fixture.state.refreshTasks();
  fixture.dispose();
  assert.equal(signal.aborted, true);
  assert.equal(fixture.timers.size, 0);
  poll.resolve([{ ...fixture.rows[0], status: 'completed' }]);
  await refresh;
  assert.equal(fixture.state.tasks.value[0].status, 'pending');
});

test('Polling errors preserve last real progress, expose freshness warning, and clear after recovery', async () => {
  const fixture = setup(1);
  await fixture.state.executeTask(fixture.state.tasks.value[0]);
  const oldJson = fixture.state.tasks.value[0].currentRunJson;
  fixture.api.listTasks = async () => { throw new Error('offline'); };
  await fixture.state.refreshTasks();
  assert.equal(fixture.state.tasks.value[0].currentRunJson, oldJson);
  assert.match(fixture.state.progressError.value, /后台测试不会/);
  fixture.api.listTasks = async () => fixture.rows;
  await fixture.state.refreshTasks();
  assert.equal(fixture.state.progressError.value, '');
  fixture.dispose();
});

test('Finishing an old start request does not cancel polling after KeepAlive reactivation', async () => {
  const fixture = setup(1);
  const start = deferred();
  fixture.api.startTask = () => start.promise;
  const executing = fixture.state.executeTask(fixture.state.tasks.value[0]);
  fixture.state.stopPolling();
  fixture.state.startPolling();
  const activeTimer = [...fixture.timers.keys()][0];
  start.resolve({ ...fixture.rows[0], status: 'running', activeRunId: 'active', currentRunJson: JSON.stringify(snapshot()) });
  await executing;
  assert.equal(fixture.timers.has(activeTimer), true);
  fixture.dispose();
});

test('Only ended snapshot-backed runs can import, never the prior run while a new run is active', () => {
  const { state, rows, dispose } = setup(1);
  const task = { ...rows[0], status: 'completed', lastRunJson: JSON.stringify({ runId: 'ended', status: 'completed', completedAt: '2026-09-29', machineSnapshot: {}, evaluationSnapshot: {}, metrics: [{ metricId: 'test' }] }) };
  assert.equal(state.canImport(task), true);
  assert.equal(state.canImport({ ...task, status: 'running' }), false);
  assert.equal(state.canImport({ ...task, lastRunJson: task.lastRunJson.replace('2026-09-29', '') }), false);
  dispose();
});
