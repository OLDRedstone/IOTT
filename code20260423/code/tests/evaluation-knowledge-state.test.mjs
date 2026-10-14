import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { computed, nextTick, reactive, ref, toRaw, watch } from 'vue';
import { compileScript, compileTemplate, parse } from '@vue/compiler-sfc';

const read = relative => readFileSync(new URL(relative, import.meta.url), 'utf8');
const modelSource = read('../src/views/verify/evaluation/knowledgeModel.ts');
const stateSource = read('../src/views/verify/evaluation/knowledgeState.ts');
function compile(source, expose) {
  return ts.transpileModule(source.replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '')
    + `\nglobalThis.exposed = { ${expose} };`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
  }).outputText;
}
const modelCode = compile(modelSource, 'createDraft, mergeTaskDraft, scoreSummary, scoreOptions, formatScore, weightErrors');
const stateCode = compile(stateSource, 'useKnowledgeRecords');
const clone = value => JSON.parse(JSON.stringify(value));
function config(category = 'machine', version = 1) {
  return { category, version, updatedAt: '', indicators: [{ id: 'category', name: 'category', weight: 100, children: [
    { id: 'child', name: 'child', weight: 100, items: [
      { id: 'auto', name: 'automatic', metricId: 'industrial-protocol', weight: 40 },
      { id: 'manual', name: 'manual', weight: 40 },
      { id: 'removed', name: 'removed manual item', weight: 20 },
    ] },
  ] }] };
}
function setup() {
  const calls = { configs: [], drafts: [], creates: [], updates: [], templates: [], confirmations: [], downloads: [] };
  const messages = { success: [], info: [], warning: [], error: [] };
  const api = {
    getConfig: async category => { calls.configs.push(category); return config(category); },
    listRecords: async () => [],
    taskDraft: async (id, category) => { calls.drafts.push({ id, category }); return syncedRecord(category ?? 'machine', id); },
    createRecord: async record => { calls.creates.push(clone(record)); return { ...clone(record), id: 'saved-new', version: 1 }; },
    updateRecord: async record => { calls.updates.push(clone(record)); return { ...clone(record), version: record.version + 1 }; },
    downloadTemplate: async category => { calls.templates.push(category); return 'fake-blob'; },
  };
  const context = vm.createContext({
    computed, ref, toRaw, Error, structuredClone, Map,
    isAxiosError: error => Boolean(error?.isAxiosError),
    ElMessage: Object.fromEntries(Object.keys(messages).map(key => [key, message => messages[key].push(message)])),
    ElMessageBox: { confirm: async (...args) => { calls.confirmations.push(args); } },
    evaluationApi: api,
    downloadBlob: (...args) => calls.downloads.push(args),
  });
  vm.runInContext(modelCode, context);
  const model = context.exposed;
  function record(category = 'machine') {
    return { ...model.createDraft(config(category)), machineName: 'user machine', machineNo: 'user number',
      machineModel: 'user model', controlSystem: 'user control', partName: 'user part', conclusion: 'keep conclusion', suggestion: 'keep suggestion' };
  }
  function syncedRecord(category = 'machine', id = 'task') {
    const snapshot = config(category, 2);
    snapshot.indicators[0].name = 'new snapshot';
    snapshot.indicators[0].children[0].items = snapshot.indicators[0].children[0].items.filter(item => item.id !== 'removed');
    return { ...record(category), snapshot, dataSource: 'sync', syncTaskId: id, syncRunId: 'run-new',
      machineName: 'run machine', scores: { auto: 0, manual: null }, testResults: { auto: '0' },
      remarks: { auto: 'current-run real evidence' }, weights: { auto: 50, manual: 50 } };
  }
  vm.runInContext(stateCode, context);
  return { context, model, state: context.exposed.useKnowledgeRecords(), calls, messages, api, record, syncedRecord };
}

test('Stable-ID merge preserves zero/custom manual scores, reports unmapped data, and never carries old provenance', () => {
  const fixture = setup();
  const previous = fixture.record();
  Object.assign(previous, { id: 'old-record', version: 7, syncTaskId: 'old-task', syncRunId: 'old-run', dataSource: 'sync' });
  previous.scores = { auto: 80, manual: 82.125, removed: 0 };
  previous.testResults.manual = 'manual result';
  previous.remarks.manual = 'manual remark';
  const before = clone(previous);
  const synced = fixture.syncedRecord();
  const { target, syncedCount, lostItems } = fixture.model.mergeTaskDraft(previous, synced);
  assert.equal(target.id, '');
  assert.equal(target.version, 0);
  assert.equal(target.syncRunId, 'run-new');
  assert.equal(target.syncTaskId, 'task');
  assert.equal(target.snapshot.version, 2);
  assert.equal(target.scores.auto, 0);
  assert.equal(target.scores.manual, 82.125);
  assert.equal(target.machineName, 'user machine');
  assert.equal(target.conclusion, 'keep conclusion');
  assert.equal(target.suggestion, 'keep suggestion');
  assert.equal(target.testResults.manual, 'manual result');
  assert.equal(syncedCount, 1);
  assert.deepEqual(clone(lostItems), ['removed manual item']);
  assert.deepEqual(clone(previous), before);
  assert.equal(synced.scores.manual, null);
});

test('A manual item that becomes automatic is not overwritten by its old manual score', () => {
  const { model, record, syncedRecord } = setup();
  const previous = record();
  previous.scores.manual = 82.125;
  const synced = syncedRecord();
  synced.snapshot.indicators[0].children[0].items.find(item => item.id === 'manual').metricId = 'communication-stability';
  synced.scores.manual = 0;
  synced.testResults.manual = 'new measurement';
  const merged = model.mergeTaskDraft(previous, synced);
  assert.equal(merged.target.scores.manual, 0);
  assert.equal(merged.target.testResults.manual, 'new measurement');
  assert.deepEqual(clone(merged.lostItems), ['manual']);
});

test('Classification contributions preserve nullable category scores and zero-weight semantics', () => {
  const { model, record } = setup();
  const draft = record();
  draft.snapshot.indicators = [
    { id: 'a', name: 'A', weight: 40, children: [{ id: 'sa', name: 'SA', weight: 100, items: [{ id: 'ia', weight: 100 }] }] },
    { id: 'b', name: 'B', weight: 60, children: [{ id: 'sb', name: 'SB', weight: 100, items: [{ id: 'ib', weight: 100 }] }] },
  ];
  draft.categoryWeights = {};
  draft.subCategoryWeights = {};
  draft.weights = {};
  draft.scores = { ia: null, ib: 80 };
  let summary = model.scoreSummary(draft);
  assert.equal(summary.total, null);
  assert.equal(summary.categoryScores.a, null);
  assert.equal(summary.categoryContributions.a, null);
  assert.equal(summary.categoryContributions.b, 48);
  draft.categoryWeights = { a: 0, b: 100 };
  summary = model.scoreSummary(draft);
  assert.equal(summary.total, 80);
  assert.equal(summary.categoryScores.a, null);
  assert.equal(summary.categoryContributions.a, 0);
  assert.equal(summary.categoryContributions.b, 80);
  draft.scores.ib = 0;
  assert.equal(model.scoreSummary(draft).total, 0);
  assert.deepEqual(clone(model.scoreOptions).map(option => option.value), [100, 80, 60, 0]);
});

test('Resynchronizing an existing record creates a new record, preserves original and asks explicit confirmation', async () => {
  const { state, calls, record } = setup();
  const original = { ...record(), id: 'original', version: 7, dataSource: 'sync', syncTaskId: 'task', syncRunId: 'run-old' };
  original.scores.manual = 0;
  original.scores.removed = 60;
  state.records.value = [original];
  state.editRecord(original);
  await state.syncTask('task');
  assert.equal(state.draft.value.id, '');
  assert.equal(state.draft.value.syncRunId, 'run-new');
  assert.equal(state.draft.value.snapshot.version, 2);
  assert.equal(state.draft.value.scores.manual, 0);
  assert.match(calls.confirmations[0][0], /另存.*原记录.*不变/);
  assert.match(calls.confirmations[0][0], /removed manual item/);
  assert.match(calls.confirmations[0][1], /保留原记录/);
  await state.saveRecord();
  assert.equal(calls.creates.length, 1);
  assert.equal(calls.updates.length, 0);
  assert.equal(state.records.value.length, 2);
  assert.equal(state.records.value.find(row => row.id === 'original').syncRunId, 'run-old');
  assert.equal(state.records.value.find(row => row.id === 'original').version, 7);
});

test('Canceled synchronization and malformed run provenance leave the existing draft untouched', async () => {
  const fixture = setup();
  const { state, context, api, record, messages } = fixture;
  state.editRecord({ ...record(), id: 'original', version: 4 });
  const previous = state.draft.value;
  const before = clone(previous);
  context.ElMessageBox.confirm = async () => { throw 'cancel'; };
  await state.syncTask('task');
  assert.equal(state.draft.value, previous);
  assert.deepEqual(clone(state.draft.value), before);
  assert.equal(messages.error.length, 0);
  api.taskDraft = async () => ({ ...fixture.syncedRecord(), syncRunId: null });
  await state.syncTask('task');
  assert.equal(state.draft.value, previous);
  assert.match(messages.error[0], /来源/);
});

test('Run deduplication and version conflicts retain drafts and never fall back to a historical update or duplicate create', async () => {
  const fixture = setup();
  const { state, api, calls, messages } = fixture;
  await state.addRecord();
  await state.syncTask('task');
  const previous = state.draft.value;
  api.createRecord = async () => { throw { isAxiosError: true, response: { status: 409, data: { error: '该任务运行结果已导入知识库' } } }; };
  await state.saveRecord();
  assert.equal(state.draft.value, previous);
  assert.equal(state.editorVisible.value, true);
  assert.equal(calls.updates.length, 0);
  assert.match(messages.error[0], /已导入.*草稿已保留/);
  state.editRecord({ ...fixture.record(), id: 'existing', version: 1 });
  api.updateRecord = async () => { throw { isAxiosError: true, response: { status: 409, data: { error: '记录版本冲突' } } }; };
  await state.saveRecord();
  assert.equal(state.draft.value.id, 'existing');
  assert.equal(state.draft.value.version, 1);
  assert.equal(calls.creates.length, 0);
  assert.match(messages.error[1], /版本冲突.*草稿已保留/);
});

test('Category switches restore independent drafts including decimal scores; loading failure preserves the active draft', async () => {
  const { state, api, calls } = setup();
  await state.addRecord();
  state.draft.value.scores.manual = 0;
  state.draft.value.partName = 'machine draft';
  const machine = state.draft.value;
  await state.changeCategory('machining');
  assert.equal(state.draft.value.category, 'machining');
  state.draft.value.scores.manual = 83.1234;
  state.draft.value.partName = 'machining draft';
  await state.changeCategory('machine');
  assert.equal(state.draft.value, machine);
  assert.equal(state.draft.value.scores.manual, 0);
  assert.equal(state.draft.value.partName, 'machine draft');
  await state.changeCategory('machining');
  assert.equal(state.draft.value.scores.manual, 83.1234);
  assert.deepEqual(calls.configs, ['machine', 'machining']);

  const other = setup();
  await other.state.addRecord();
  const retained = other.state.draft.value;
  other.api.getConfig = async () => { throw new Error('configuration unavailable'); };
  await other.state.changeCategory('machining');
  assert.equal(other.state.draft.value, retained);
  assert.equal(other.state.currentCategory.value, 'machine');
  assert.match(other.messages.error[0], /configuration unavailable/);
});

test('Saving an existing record does not erase a separate unsaved draft of the same category', async () => {
  const { state, record } = setup();
  await state.addRecord();
  state.draft.value.partName = 'unsaved part';
  state.draft.value.scores.manual = 81.2345;
  state.editRecord({ ...record(), id: 'existing', version: 1 });
  await state.saveRecord();
  await state.addRecord();
  assert.equal(state.draft.value.id, '');
  assert.equal(state.draft.value.partName, 'unsaved part');
  assert.equal(state.draft.value.scores.manual, 81.2345);
});

test('Creation and template downloads default to the selected category; deep links use the recorded run category', async () => {
  const fixture = setup();
  const { state, calls, api } = fixture;
  await state.changeCategory('machining');
  await state.addRecord();
  assert.equal(state.draft.value.category, 'machining');
  await state.downloadTemplate();
  await state.downloadTemplate('machine');
  assert.deepEqual(calls.templates, ['machining', 'machine']);
  assert.match(calls.downloads[0][1], /加工评价/);

  const deepLink = setup();
  deepLink.api.taskDraft = async (id, category) => {
    assert.equal(category, undefined);
    return deepLink.syncedRecord('machining', id);
  };
  await deepLink.state.openTaskDraft('task');
  assert.equal(deepLink.state.draft.value.category, 'machining');
  assert.equal(deepLink.state.currentCategory.value, 'machining');
  assert.equal(deepLink.state.draft.value.syncRunId, 'run-new');
});

const transferSource = read('../src/views/verify/evaluation/knowledgeTransfer.vue');
const transferScript = parse(transferSource).descriptor.scriptSetup.content;
function setupTransfer() {
  const props = reactive({ taskVisible: false, importVisible: false, syncing: false, category: 'machining' });
  const events = [];
  const warnings = [];
  const rows = [
    { id: 'machine', category: 'machine', name: 'machine', machineName: 'M', machineNo: '1', metrics: [{ metricId: 'one', score: 0 }] },
    { id: 'machining', category: 'machining', name: 'machining', machineName: 'C', machineNo: '2', metrics: [{ metricId: 'two', score: null }] },
  ];
  let imports = 0;
  const api = { listTasks: async () => rows, importRecords: async () => { imports++; return { total: 2, success: 1, failed: 1, errors: ['第 3 行：评分无效'] }; } };
  const context = vm.createContext({
    computed, ref, watch, structuredClone, Error,
    defineProps: () => props, defineEmits: () => (...args) => events.push(args),
    evaluationApi: api, ElMessage: { warning: message => warnings.push(message), error: message => warnings.push(message) },
  });
  vm.runInContext(modelCode, context);
  vm.runInContext(compile(transferScript, 'filteredTasks, selectedTask, selectedTaskRecord, taskCategory, taskSearch, selectFile, selectedFile, importFile, importResult, imported, confirmTask, formatPassRate'), context);
  return { props, state: context.exposed, api, events, warnings, imports: () => imports };
}
async function flush() { await nextTick(); await Promise.resolve(); await nextTick(); }

test('Task picker defaults to the current category, filters choices safely and formats nullable or zero pass rates', async () => {
  const { props, state, events } = setupTransfer();
  props.taskVisible = true;
  await flush();
  assert.equal(state.taskCategory.value, 'machining');
  assert.deepEqual(clone(state.filteredTasks.value).map(row => row.id), ['machining']);
  state.selectedTask.value = 'machining';
  state.confirmTask();
  assert.deepEqual(events[0], ['sync', 'machining', 'machining']);
  state.taskCategory.value = 'machine';
  assert.equal(state.selectedTaskRecord.value, undefined);
  state.confirmTask();
  assert.equal(events.length, 1);
  assert.equal(state.formatPassRate(0), '0.0%');
  assert.equal(state.formatPassRate(100), '100.0%');
  assert.match(state.formatPassRate(null), /未配置.*未确定/);
});

test('Both Excel formats are accepted and partial row errors remain visible without retrying successful rows', async () => {
  const { state, imports, events } = setupTransfer();
  state.selectFile({ name: 'records.XLS', raw: { size: 100, fake: true } });
  assert.ok(state.selectedFile.value);
  await state.importFile();
  assert.equal(imports(), 1);
  assert.equal(state.importResult.value.success, 1);
  assert.deepEqual(clone(state.importResult.value.errors), ['第 3 行：评分无效']);
  assert.equal(state.imported.value, true);
  assert.deepEqual(events, [['imported']]);
  await state.importFile();
  assert.equal(imports(), 1);
  state.selectFile({ name: 'corrected.xlsx', raw: { size: 100, fake: true } });
  assert.ok(state.selectedFile.value);
  assert.equal(state.imported.value, false);
});

test('Excel selection rejects other extensions and missing files without making an import request', async () => {
  const { state, imports, warnings } = setupTransfer();
  state.selectFile({ name: 'records.csv', raw: { size: 100 } });
  assert.equal(state.selectedFile.value, null);
  state.selectFile({ name: 'missing.xls' });
  assert.equal(state.selectedFile.value, null);
  await state.importFile();
  assert.equal(imports(), 0);
  assert.equal(warnings.length, 2);
});

for (const relative of ['../src/views/verify/EvaluationKnowledgeView.vue',
  '../src/views/verify/evaluation/knowledgeEditor.vue', '../src/views/verify/evaluation/knowledgeScores.vue',
  '../src/views/verify/evaluation/knowledgeTransfer.vue']) {
  test(`Vue script and template compile for ${relative.split('/').at(-1)}`, () => {
    const { descriptor, errors } = parse(read(relative), { filename: relative });
    assert.deepEqual(errors, []);
    const script = compileScript(descriptor, { id: relative });
    const template = compileTemplate({ source: descriptor.template.content, filename: relative, id: relative,
      compilerOptions: { bindingMetadata: script.bindings } });
    assert.deepEqual(template.errors, []);
  });
}
