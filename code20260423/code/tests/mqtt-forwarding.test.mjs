import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, ref } from "vue";

function compile(relativePath, names, select = false) {
    const source = readFileSync(new URL(relativePath, import.meta.url), "utf8");
    const script = source.match(/<script[^>]*setup[^>]*>([\s\S]*?)<\/script>/)[1];
    const parsed = ts.createSourceFile("fixture.ts", script, ts.ScriptTarget.Latest, true);
    const statements = parsed.statements.filter(statement => {
        if (ts.isImportDeclaration(statement)) return false;
        if (!select) return true;
        const name = ts.isFunctionDeclaration(statement) ? statement.name?.text
            : ts.isVariableStatement(statement) ? statement.declarationList.declarations[0]?.name.getText(parsed) : null;
        return names.includes(name);
    });
    return ts.transpileModule(statements.map(statement => statement.getText(parsed)).join("\n")
        + `\nglobalThis.fixture = { ${names.join(",")} };`, {
        compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
    }).outputText;
}

const batchCode = compile("../src/views/industrial/DeviceView.vue", ["collectionStorageErrors",
    "collectionMqttErrors", "collectionUploadErrors", "collectionBatchResults",
    "setCollectionStorageError", "applyCollectionBatchResult", "runCollectionBatch"], true);
const pollCode = compile("../src/components/MqttForwardingStatus.vue", [
    "visible", "loading", "overview", "fetchError", "refresh", "connectionText", "connectionTagType",
]);
const flush = () => new Promise(resolve => setImmediate(resolve));
const batchResult = { written: 1, storageEnabled: true, mqttEnabled: true, mqttQueued: true, mqttPublished: false };

function batchSetup(writeBatch = async () => batchResult) {
    const messages = [];
    const context = vm.createContext({ ref, devices: ref([{ id: "device", name: "设备" }]),
        ElMessage: { warning: message => messages.push(message) },
        machineConnectionPointsApi: { readTags: async () => ({ tags: [] }) },
        telemetryInfluxApi: { writeBatch }, normalizeTagAddress: value => value,
        stringifyTagValue: String, upsertCollectionRowsByDevice() {}, upsertCollectionRows() {},
        collectionDeviceId: ref("device"), getApiErrorMessage: error => error.message,
    });
    vm.runInContext(batchCode, context);
    return { ...context.fixture, messages };
}

function pollSetup(getMqttStatus) {
    let onVisible;
    let unmount;
    const timers = new Map();
    let nextTimer = 0;
    const context = vm.createContext({ ref, computed, AbortController,
        telemetryInfluxApi: { getMqttStatus }, watch: (_source, callback) => { onVisible = callback; },
        onBeforeUnmount: callback => { unmount = callback; },
        setTimeout: (callback, delay) => { assert.equal(delay, 5000); timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout: timer => timers.delete(timer),
    });
    vm.runInContext(pollCode, context);
    return { ...context.fixture, timers, unmount: () => unmount(),
        show(open) { context.fixture.visible.value = open; onVisible(open); },
        tick() { const [key, callback] = timers.entries().next().value; timers.delete(key); callback(); },
    };
}

test("Disabled history storage does not report an error when MQTT accepts the batch", () => {
    const fixture = batchSetup();
    fixture.collectionStorageErrors.value.device = "previous error";
    fixture.applyCollectionBatchResult("device", { ...batchResult, storageEnabled: false, written: 0 }, 1);
    assert.equal(fixture.collectionStorageErrors.value.device, undefined);
    assert.equal(fixture.collectionMqttErrors.value.device, undefined);
    assert.equal(fixture.collectionBatchResults.value.device.mqttPublished, false);
    assert.equal(fixture.collectionBatchResults.value.device.mqttQueued, true);
    assert.equal(fixture.messages.length, 0);
});

test("Storage and MQTT failures are reported separately and clear after recovery", () => {
    const fixture = batchSetup();
    fixture.applyCollectionBatchResult("device", { ...batchResult, written: 0, storageError: "Influx offline" }, 1);
    assert.equal(fixture.collectionStorageErrors.value.device, "Influx offline");
    assert.equal(fixture.collectionMqttErrors.value.device, undefined);
    fixture.applyCollectionBatchResult("device", { ...batchResult, mqttQueued: false, mqttError: "Disk full" }, 1);
    assert.equal(fixture.collectionStorageErrors.value.device, undefined);
    assert.equal(fixture.collectionMqttErrors.value.device, "Disk full");
    fixture.applyCollectionBatchResult("device", batchResult, 1);
    assert.equal(fixture.collectionMqttErrors.value.device, undefined);
});

test("Batch HTTP errors retain structured storage and MQTT results", async () => {
    const result = { ...batchResult, written: 0, storageError: "Influx offline", mqttQueued: false, mqttError: "Disk full" };
    const fixture = batchSetup(async () => { throw { response: { status: 502, data: result } }; });
    await fixture.runCollectionBatch("device", [{ address: "value", path: "value", dataType: "Int32", displayName: "Value" }]);
    await flush();
    assert.equal(fixture.collectionStorageErrors.value.device, "Influx offline");
    assert.equal(fixture.collectionMqttErrors.value.device, "Disk full");
    assert.equal(fixture.collectionUploadErrors.value.device, undefined);
});

test("Network failures are unconfirmed uploads, not an asserted storage or MQTT failure", async () => {
    const fixture = batchSetup(async () => { throw new Error("Network timeout"); });
    fixture.applyCollectionBatchResult("device", batchResult, 1);
    await fixture.runCollectionBatch("device", [{ address: "value", path: "value", dataType: "Int32", displayName: "Value" }]);
    await flush();
    assert.equal(fixture.collectionUploadErrors.value.device, "Network timeout");
    assert.equal(fixture.collectionBatchResults.value.device, undefined);
    assert.equal(fixture.collectionStorageErrors.value.device, undefined);
    assert.equal(fixture.collectionMqttErrors.value.device, undefined);
});

test("Missing point responses are forwarded as failed points with an explicit error", async () => {
    const batches = [];
    const fixture = batchSetup(async batch => { batches.push(batch); return batchResult; });
    await fixture.runCollectionBatch("device", [{ address: "missing", path: "missing", dataType: "Int32", displayName: "Missing" }]);
    await flush();
    assert.equal(batches[0].points.length, 1);
    assert.equal(batches[0].points[0].value, null);
    assert.equal(batches[0].points[0].status, "失败");
    assert.equal(batches[0].points[0].errorMessage, "响应中缺少该点位");
});

test("Status polling prevents overlap, aborts on close and ignores the old response after reopening", async () => {
    const requests = [];
    const fixture = pollSetup(signal => new Promise(resolve => requests.push({ signal, resolve })));
    fixture.show(true);
    await fixture.refresh();
    assert.equal(requests.length, 1);
    fixture.show(false);
    assert.equal(requests[0].signal.aborted, true);
    fixture.show(true);
    requests[0].resolve({ gateway: { pendingCount: 100 }, host: null });
    await flush();
    assert.equal(fixture.overview.value, null);
    requests[1].resolve({ gateway: { pendingCount: 2 }, host: null });
    await flush();
    assert.equal(fixture.overview.value.gateway.pendingCount, 2);
    assert.equal(fixture.timers.size, 1);
    fixture.unmount();
    assert.equal(fixture.timers.size, 0);
});

test("Status failures preserve the last result, display the error and recover on the next poll", async () => {
    let fail = false;
    const fixture = pollSetup(async () => {
        if (fail) throw new Error("Status unavailable");
        return { gateway: { pendingCount: 2 }, host: null, hostError: "Host unavailable" };
    });
    fixture.show(true);
    await flush();
    fail = true;
    fixture.tick();
    await flush();
    assert.equal(fixture.fetchError.value, "Status unavailable");
    assert.equal(fixture.overview.value.gateway.pendingCount, 2);
    assert.equal(fixture.overview.value.hostError, "Host unavailable");
    fail = false;
    fixture.tick();
    await flush();
    assert.equal(fixture.fetchError.value, "");
    fixture.unmount();
});

test("Idle disconnected status is informational; unmount aborts the current request", async () => {
    let signal;
    const fixture = pollSetup(current => { signal = current; return new Promise(() => {}); });
    const idle = { enabled: true, isConnected: false, pendingCount: 0 };
    assert.equal(fixture.connectionTagType(idle), "info");
    assert.equal(fixture.connectionText(idle), "未连接");
    assert.equal(fixture.connectionTagType({ ...idle, pendingCount: 1 }), "warning");
    fixture.show(true);
    fixture.unmount();
    assert.equal(signal.aborted, true);
    assert.equal(fixture.timers.size, 0);
});
