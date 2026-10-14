import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { computed, nextTick, reactive, ref, watch } from 'vue';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';

const ruleSource = readFileSync(new URL('../src/views/verify/evaluation/automationRules.ts', import.meta.url), 'utf8');
const editorSource = readFileSync(new URL('../src/views/verify/evaluation/indicatorThresholdEditor.vue', import.meta.url), 'utf8');
const transpile = source => ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;
function fixture(metricId, supplied) {
  const context = vm.createContext({ computed, nextTick, reactive, ref, watch });
  vm.runInContext(transpile(ruleSource.replace(/^import[^\n]*$/gm, '').replace(/^export /gm, '')), context);
  const item = reactive(supplied ?? { id: 'fixture', metricId, automation: context.defaultFor(metricId), protocols: [] });
  context.defineProps = () => ({ item });
  const script = editorSource.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)[1];
  vm.runInContext(transpile('(() => {\n' + script.replace(/^import[^\n]*$/gm, '')
    + '\nglobalThis.editor = { setThreshold, updateCriterion, enableAutomation, comparison, unit, pass };\n})();'), context);
  return { item, rules: context, editor: context.editor };
}

test('All seven automatic indicators accept an explicitly entered threshold, retain zero and clear to unconfigured', () => {
  for (const metricId of ['industrial-protocol', 'communication-stability', 'max-connections', 'transfer-protocol', 'file-integrity', 'transfer-speed', 'file-size']) {
    const { item, editor, rules } = fixture(metricId);
    assert.equal(item.automation.passRule?.threshold ?? null, metricId === 'max-connections' ? 100 : null);
    editor.setThreshold(5);
    assert.equal(item.automation.passRule.threshold, 5);
    assert.equal(rules.validate(item), undefined);
    assert.equal(item.manualPassRule, undefined);
    editor.setThreshold(0);
    assert.equal(item.automation.passRule.threshold, 0);
    assert.match(rules.thresholdSummary(item), /≥ 0/);
    editor.setThreshold(undefined);
    assert.equal(item.automation.passRule, null);
    assert.equal(rules.thresholdSummary(item), '未配置');
  }
});

test('Threshold comparison and independent units are editable without changing score bands', () => {
  const { item, editor, rules } = fixture('transfer-speed');
  const bands = JSON.stringify(item.automation.scoreBands);
  editor.comparison.value = 'lte';
  editor.unit.value = 'KB/s';
  editor.setThreshold(200);
  assert.equal(item.automation.passRule.comparison, 'lte');
  assert.equal(item.automation.passRule.unit, 'KB/s');
  assert.equal(item.automation.unit, 'MB/s');
  assert.equal(JSON.stringify(item.automation.scoreBands), bands);
  assert.equal(rules.validate(item), undefined);
});

test('Opening a disabled indicator does not enable a zero threshold or bypass explicit activation', () => {
  const { item, editor } = fixture('file-integrity', { id: 'disabled', metricId: 'file-integrity', automation: null });
  editor.setThreshold(100);
  assert.equal(item.automation, null);
  editor.enableAutomation();
  assert.equal(item.automation.passRule, null);
  editor.setThreshold(100);
  assert.equal(item.automation.passRule.threshold, 100);
});

test('Ordinary indicators save custom-unit manual criteria without adding automation or scores', () => {
  const { item, editor, rules } = fixture(undefined);
  editor.comparison.value = 'gte';
  editor.unit.value = '℃';
  editor.setThreshold(-10);
  assert.equal(item.manualPassRule.threshold, -10);
  assert.equal(item.manualPassRule.unit, '℃');
  assert.equal(item.automation, null);
  assert.equal(item.scores, undefined);
  assert.equal(rules.validate(item), undefined);
  assert.match(rules.thresholdSummary(item), /人工核验/);
  assert.match(rules.ruleSummary(item), /-10 ℃/);
  editor.setThreshold(null);
  assert.equal(item.manualPassRule, null);
});

test('Manual thresholds reject missing units, non-finite numbers and a second threshold on automatic items', () => {
  const { item, editor, rules } = fixture(undefined);
  editor.setThreshold(1);
  assert.match(rules.validate(item), /单位/);
  editor.unit.value = 'mm';
  editor.setThreshold(Infinity);
  assert.equal(typeof rules.validate(item), 'string');
  editor.setThreshold(0);
  assert.equal(rules.validate(item), undefined);
  item.metricId = 'file-size';
  item.automation = rules.defaultFor(item.metricId);
  assert.match(rules.validate(item), /同时配置人工阈值/);
});

test('Existing persisted criteria populate the editor without overwriting the value or unit', async () => {
  const { item, editor } = fixture(undefined, { id: 'saved', metricId: null,
    manualPassRule: { comparison: 'lte', threshold: 0.02, unit: 'mm' } });
  assert.equal(editor.unit.value, 'mm');
  assert.equal(editor.comparison.value, 'lte');
  editor.setThreshold(0.01);
  await nextTick();
  assert.equal(item.manualPassRule.threshold, 0.01);
  assert.equal(editor.unit.value, 'mm');
});

test('Threshold editor, parent form and knowledge threshold display compile as Vue components', () => {
  for (const filename of ['indicatorThresholdEditor.vue', 'metricEditor.vue', 'knowledgeScores.vue']) {
    const source = readFileSync(new URL(`../src/views/verify/evaluation/${filename}`, import.meta.url), 'utf8');
    const { descriptor, errors } = parse(source, { filename });
    assert.deepEqual(errors, []);
    const script = compileScript(descriptor, { id: filename });
    const template = compileTemplate({ source: descriptor.template.content, filename, id: filename,
      compilerOptions: { bindingMetadata: script.bindings } });
    assert.deepEqual(template.errors, []);
  }
});
