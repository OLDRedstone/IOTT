import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import ts from "typescript";
import { computed, effectScope, nextTick, reactive, ref, watch } from "vue";
import { compileScript, compileTemplate, parse } from "@vue/compiler-sfc";
import TreeStore from "element-plus/es/components/tree/src/model/tree-store.mjs";

const filename = "DeviceConfigView.vue";
const component = readFileSync(new URL("../src/views/plc/DeviceConfigView.vue", import.meta.url), "utf8");
const { descriptor, errors } = parse(component, { filename });
assert.equal(errors.length, 0);
const s7 = {};
vm.runInNewContext(ts.transpileModule(readFileSync(new URL("../src/utils/plcS7Configuration.ts", import.meta.url), "utf8"),
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText, { exports: s7 });
const script = descriptor.scriptSetup.content;
const parsed = ts.createSourceFile(filename, script, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const names = ["loadCapabilities", "capabilities", "capabilitiesError", "protocolOptions", "protocolGroups",
    "currentDevice", "blankDevice", "handleProtocolChange", "saveDevice", "mapToPlc", "brandOptions",
    "openAddDeviceDialog", "openEditDeviceDialog", "openCapabilitiesDialog", "refreshDeviceList",
    "treeKeyword", "treeRef", "deviceTree", "filterTreeNode", "isSerialConnection", "s7Defaults"];
const executable = ts.transpileModule(
    parsed.statements.filter((statement) => !ts.isImportDeclaration(statement))
        .map((statement) => statement.getText(parsed)).join("\n")
        + "\nglobalThis.handlers = { " + names.join(",") + " };",
    { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None } },
).outputText;

function setup(context, fetchCapabilities = async () => [{ brand: "欧姆龙", protocols: ["FINS"] }]) {
    const writes = [];
    const warnings = [];
    const scope = effectScope();
    const capture = (method) => async (...args) => {
        writes.push({ method, body: JSON.parse(JSON.stringify(args.at(-1))) });
        return { upstreamSynced: true };
    };
    const sandbox = vm.createContext({
        ...s7,
        computed, reactive, ref, watch, onMounted() {}, useRouter: () => ({ push() {} }),
        ElMessage: { error() {}, success() {}, warning: (message) => warnings.push(message) },
        machineConnectionDevicesApi: { list: async () => [], create: capture("create"), update: capture("update") },
        machineConnectionDiagnosticsApi: { plcCapabilities: fetchCapabilities },
    });
    scope.run(() => vm.runInContext(executable, sandbox));
    context.after(() => scope.stop());
    return { handlers: sandbox.handlers, writes, warnings };
}

function fillNew(handlers, protocol) {
    Object.assign(handlers.currentDevice, handlers.blankDevice(), {
        deviceCode: "PLC-1", name: "PLC", model: "CJ2M-CPU33", protocol,
        ip: "192.0.2.10", port: 9600, series: "H3U",
    });
    handlers.handleProtocolChange(protocol);
    if (protocol === "SiemensS7") handlers.currentDevice.s7CpuType = "S1200";
}

async function flush() {
    await nextTick();
    await new Promise((resolve) => setImmediate(resolve));
}

for (const [protocol, brand, properties] of [
    ["FINS", "Omron", { DA1: "12", SA1: "100", DataFormat: "BADC" }],
    ["SiemensS7", "Siemens", { PlcType: "S1500", Rack: "0", Slot: "1", StringLength: "40" }],
    ["OmronHostLink", "Omron", { PortName: "COM4", UnitNumber: "2" }],
    ["MewtocolSerial", "Panasonic", { PortName: "COM8", Parity: "Odd" }],
]) {
    test(protocol + " edits preserve the stored brand and driver properties", async (context) => {
        const fixture = setup(context);
        const original = { id: "existing", name: "PLC", brand, model: "Model-1", protocol,
            host: "192.0.2.10", port: 9600, status: "Offline",
            extendedProperties: { deviceCode: "PLC-1", station: "2", ...properties } };
        fixture.handlers.openEditDeviceDialog(fixture.handlers.mapToPlc(original));
        await fixture.handlers.saveDevice();
        assert.equal(fixture.writes.length, 1);
        const { method, body } = fixture.writes[0];
        assert.equal(method, "update");
        assert.equal(body.brand, brand);
        for (const [key, value] of Object.entries(properties)) assert.equal(body.extendedProperties[key], value);
        assert.equal(body.extendedProperties.Station, "2");
        assert.equal(original.extendedProperties.station, "2");
        if (protocol === "OmronHostLink") assert.equal(body.extendedProperties.Mode, "Serial");
    });
}

test("Changing protocol clears parameters that belong to the previous driver", async (context) => {
    const fixture = setup(context);
    fixture.handlers.openEditDeviceDialog(fixture.handlers.mapToPlc({
        id: "existing", name: "PLC", brand: "Siemens", model: "Model-1", protocol: "SiemensS7",
        host: "192.0.2.10", port: 102, status: "Offline",
        extendedProperties: { deviceCode: "PLC-1", PlcType: "S1500", Series: "S1500" },
    }));
    fixture.handlers.currentDevice.protocol = "FINS";
    fixture.handlers.handleProtocolChange("FINS");
    await fixture.handlers.saveDevice();
    assert.equal(fixture.writes[0].body.brand, "欧姆龙");
    assert.equal(fixture.writes[0].body.extendedProperties.PlcType, undefined);
    assert.equal(fixture.writes[0].body.extendedProperties.Series, undefined);
});

for (const action of ["openAddDeviceDialog", "openEditDeviceDialog", "openCapabilitiesDialog", "refreshDeviceList"]) {
    test(action + " retries failed capability loading while preserving fallback choices", async (context) => {
        let attempts = 0;
        const fixture = setup(context, async () => {
            if (++attempts === 1) throw new Error("temporary outage");
            return [{ brand: "欧姆龙", protocols: ["FINS"] }];
        });
        await fixture.handlers.loadCapabilities();
        assert.equal(fixture.handlers.capabilitiesError.value, true);
        for (const protocol of ["ModbusTCP", "ModbusRTU", "SiemensS7", "Inovance", "MewtocolSerial"]) {
            assert.ok(fixture.handlers.protocolOptions.value.some((option) => option.value === protocol));
        }
        await fixture.handlers[action](fixture.handlers.blankDevice());
        await flush();
        assert.equal(attempts, 2);
        assert.equal(fixture.handlers.capabilitiesError.value, false);
        assert.equal(fixture.handlers.capabilities.value[0].brand, "欧姆龙");
    });
}

test("Simultaneous capability requests share one in-flight request", async (context) => {
    let complete;
    let attempts = 0;
    const fixture = setup(context, () => {
        attempts++;
        return new Promise((resolve) => { complete = resolve; });
    });
    const loading = fixture.handlers.loadCapabilities();
    fixture.handlers.openAddDeviceDialog();
    fixture.handlers.openCapabilitiesDialog();
    assert.equal(attempts, 1);
    complete([{ brand: "欧姆龙", protocols: ["FINS"] }]);
    await loading;
    assert.equal(fixture.handlers.capabilitiesError.value, false);
});

test("Protocol options retain all brands, deduplicate groups, and reapply Chinese or protocol filters", async (context) => {
    const { handlers } = setup(context);
    const tree = new TreeStore({ key: "id", data: handlers.deviceTree.value,
        props: { children: "children", label: "label" }, filterNodeMethod: handlers.filterTreeNode });
    tree.initialize();
    handlers.treeRef.value = { filter: (keyword) => tree.filter(keyword) };
    const stop = watch(handlers.deviceTree, (data) => tree.setData(data), { flush: "pre" });
    context.after(stop);
    const visible = () => tree.root.childNodes[0].childNodes.filter((node) => node.visible).map((node) => node.data.id);
    handlers.treeKeyword.value = "siemens";
    await flush();
    assert.deepEqual(visible(), ["protocol-SiemensS7"]);
    handlers.capabilities.value = [
        { brand: "欧姆龙", protocols: ["FINS", "ModbusTCP"] },
        { brand: "第二品牌", protocols: ["FINS", "ModbusTCP", "ModbusRTU"] },
        { brand: "西门子", protocols: ["SiemensS7", "OpcUa"] },
    ];
    await flush();
    assert.deepEqual(visible(), ["protocol-SiemensS7"]);
    const options = Array.from(handlers.protocolGroups.value).flatMap((group) => Array.from(group.protocols));
    assert.equal(options.length, new Set(options.map((option) => option.value)).size);
    const common = handlers.protocolGroups.value.find((group) => group.brand === "通用协议");
    assert.deepEqual(Array.from(common.protocols, (option) => option.value), ["ModbusTCP", "ModbusRTU"]);
    fillNew(handlers, "FINS");
    assert.ok(handlers.brandOptions.value.includes("欧姆龙"));
    assert.ok(handlers.brandOptions.value.includes("第二品牌"));
    handlers.treeKeyword.value = "欧姆龙";
    await flush();
    assert.deepEqual(visible(), ["protocol-FINS"]);
});

test("The PLC device component script and template compile", () => {
    const compiled = compileScript(descriptor, { id: "plc-device-config" });
    const template = compileTemplate({ source: descriptor.template.content, filename,
        id: "plc-device-config", compilerOptions: { bindingMetadata: compiled.bindings } });
    assert.deepEqual(template.errors, []);
});

for (const [protocol, brand] of [["FINS", "欧姆龙"], ["OmronHostLink", "欧姆龙"],
    ["Mewtocol", "松下"], ["SiemensS7", "西门子"]]) {
    test(protocol + " saves its registered brand independently of the model", async (context) => {
        const fixture = setup(context);
        fillNew(fixture.handlers, protocol);
        await fixture.handlers.saveDevice();
        assert.equal(fixture.writes.length, 1);
        assert.equal(fixture.writes[0].body.brand, brand);
        assert.equal(fixture.writes[0].body.model, "CJ2M-CPU33");
    });
}

for (const [protocol, brand] of [["ModbusRTU", "PLC"], ["InovanceSerial", "汇川"], ["MewtocolSerial", "松下"]]) {
    test(protocol + " requires and saves all serial connection parameters", async (context) => {
        const fixture = setup(context);
        fillNew(fixture.handlers, protocol);
        fixture.handlers.currentDevice.ip = "";
        await fixture.handlers.saveDevice();
        assert.equal(fixture.writes.length, 0);
        assert.equal(fixture.warnings.length, 1);
        Object.assign(fixture.handlers.currentDevice, {
            portName: " COM9 ", baudRate: 115200, dataBits: 7, stopBits: "Two", parity: "Even",
        });
        await fixture.handlers.saveDevice();
        const body = fixture.writes[0].body;
        assert.equal(body.brand, brand);
        assert.equal(body.host, "127.0.0.1");
        assert.deepEqual(Object.fromEntries(["PortName", "BaudRate", "DataBits", "StopBits", "Parity"]
            .map((key) => [key, body.extendedProperties[key]])),
        { PortName: "COM9", BaudRate: "115200", DataBits: "7", StopBits: "Two", Parity: "Even" });
    });
}

test("S7 legacy aliases round-trip through the actual edit and save handlers", async (context) => {
    const fixture = setup(context);
    const original = { id: "s7", name: "PLC", brand: "Siemens", model: "CPU part number", protocol: "SiemensS7",
        host: "192.0.2.10", port: 102, status: "Offline",
        extendedProperties: { deviceCode: "S7-1", cputype: "s7-300", rack: "1", slot: "5", StringLength: "40" } };
    fixture.handlers.openEditDeviceDialog(fixture.handlers.mapToPlc(original));
    assert.equal(fixture.handlers.currentDevice.s7CpuType, "S300");
    assert.equal(fixture.handlers.currentDevice.s7Rack, 1);
    assert.equal(fixture.handlers.currentDevice.s7Slot, 5);
    await fixture.handlers.saveDevice();
    const properties = fixture.writes[0].body.extendedProperties;
    assert.equal(properties.PlcType, "S300");
    assert.equal(properties.Rack, "1");
    assert.equal(properties.Slot, "5");
    assert.equal(properties.StringLength, "40");
    assert.equal(properties.cputype, undefined);
    assert.equal(properties.rack, undefined);
    assert.equal(original.extendedProperties.cputype, "s7-300");
});

test("S7 automatic CPU and omitted slot follow model changes after an edit round-trip", async (context) => {
    const fixture = setup(context);
    fillNew(fixture.handlers, "SiemensS7");
    Object.assign(fixture.handlers.currentDevice, { model: "S7-300", s7CpuType: "", port: 502 });
    fixture.handlers.handleProtocolChange("SiemensS7");
    assert.equal(fixture.handlers.currentDevice.port, 102);
    assert.equal(fixture.handlers.s7Defaults.value.slot, 2);
    await fixture.handlers.saveDevice();
    const body = fixture.writes[0].body;
    assert.equal(body.extendedProperties.PlcType, undefined);
    assert.equal(body.extendedProperties.Slot, undefined);
    fixture.handlers.openEditDeviceDialog(fixture.handlers.mapToPlc({ ...body, id: "s7", status: "Offline" }));
    fixture.handlers.currentDevice.model = "S7-400";
    assert.equal(fixture.handlers.s7Defaults.value.slot, 3);
    await fixture.handlers.saveDevice();
    assert.equal(fixture.writes[1].body.model, "S7-400");
    assert.equal(fixture.writes[1].body.extendedProperties.Slot, undefined);
    fixture.handlers.currentDevice.s7CpuType = "S200Smart";
    assert.equal(fixture.handlers.s7Defaults.value.slot, 0);
});

test("S7 protocol switching preserves explicit edits without leaking settings to another driver", async (context) => {
    const fixture = setup(context);
    fixture.handlers.openEditDeviceDialog(fixture.handlers.mapToPlc({ id: "s7", name: "PLC", brand: "Siemens",
        model: "S7-300", protocol: "SiemensS7", host: "192.0.2.10", port: 102, status: "Offline",
        extendedProperties: { deviceCode: "S7-1", PlcType: "S300", Rack: "0", Slot: "0", StringLength: "40" } }));
    fixture.handlers.currentDevice.s7CpuType = "S400";
    fixture.handlers.currentDevice.protocol = "FINS";
    fixture.handlers.handleProtocolChange("FINS");
    await fixture.handlers.saveDevice();
    assert.equal(fixture.writes[0].body.extendedProperties.PlcType, undefined);
    assert.equal(fixture.writes[0].body.extendedProperties.Slot, undefined);
    fixture.handlers.currentDevice.protocol = "SiemensS7";
    fixture.handlers.handleProtocolChange("SiemensS7");
    await fixture.handlers.saveDevice();
    const properties = fixture.writes[1].body.extendedProperties;
    assert.equal(properties.PlcType, "S400");
    assert.equal(properties.Rack, "0");
    assert.equal(properties.Slot, "0");
    assert.equal(properties.StringLength, "40");
    fixture.handlers.openAddDeviceDialog();
    assert.equal(fixture.handlers.currentDevice.s7CpuType, "");
    assert.equal(fixture.handlers.currentDevice.s7Slot, undefined);
});

for (const fields of [{ s7CpuType: "S7301" }, { s7Rack: -1 }, { s7Rack: 8 }, { s7Slot: 32 },
    { s7Slot: 1.5 }, { s7CpuType: "S200Smart", s7Slot: 1 }, { s7CpuType: "", model: "Unknown CPU" }]) {
    test("S7 invalid parameters are rejected before saving: " + JSON.stringify(fields), async (context) => {
        const fixture = setup(context);
        fillNew(fixture.handlers, "SiemensS7");
        Object.assign(fixture.handlers.currentDevice, fields);
        await fixture.handlers.saveDevice();
        assert.equal(fixture.writes.length, 0);
        assert.equal(fixture.warnings.length, 1);
    });
}

test("HostLink serial mode is required explicitly and TCP mode omits serial settings", async (context) => {
    const fixture = setup(context);
    fillNew(fixture.handlers, "OmronHostLink");
    fixture.handlers.currentDevice.hostLinkMode = "Serial";
    await fixture.handlers.saveDevice();
    assert.equal(fixture.writes.length, 0);
    fixture.handlers.currentDevice.portName = "COM5";
    await fixture.handlers.saveDevice();
    const serial = fixture.writes[0].body.extendedProperties;
    assert.equal(serial.Mode, "Serial");
    assert.equal(serial.DataBits, "7");
    assert.equal(serial.StopBits, "Two");
    assert.equal(serial.Parity, "Even");
    fixture.handlers.currentDevice.hostLinkMode = "Tcp";
    await fixture.handlers.saveDevice();
    const tcp = fixture.writes[1].body.extendedProperties;
    assert.equal(tcp.Mode, "Tcp");
    assert.equal(tcp.PortName, undefined);
    assert.equal(tcp.BaudRate, undefined);
});
