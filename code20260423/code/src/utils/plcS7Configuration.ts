export interface S7FormFields {
	s7CpuType: string;
	s7Rack?: number | null;
	s7Slot?: number | null;
}

export const S7_CPU_TYPES = [
	{ value: "S1200", label: "S7-1200" },
	{ value: "S1500", label: "S7-1500" },
	{ value: "S300", label: "S7-300" },
	{ value: "S400", label: "S7-400" },
	{ value: "S200Smart", label: "S7-200Smart" },
	{ value: "S200", label: "S7-200" },
] as const;

function normalizeCpu(raw: string): string | undefined {
	const compact = raw.replace(/[\s_-]/g, "").toUpperCase();
	return S7_CPU_TYPES.find(({ value, label }) =>
		value.toUpperCase() === compact || label.replace("-", "").toUpperCase() === compact)?.value;
}

function property(properties: Record<string, string>, ...keys: string[]): string | undefined {
	for (const key of keys) {
		if (Object.prototype.hasOwnProperty.call(properties, key)) return properties[key];
		const match = Object.keys(properties).find((candidate) => candidate.toLowerCase() === key.toLowerCase());
		if (match !== undefined) return properties[match];
	}
	return undefined;
}

export function restoreS7Fields(properties: Record<string, string> = {}): S7FormFields {
	const raw = property(properties, "PlcType", "CpuType", "Series");
	const number = (key: string) => {
		const value = property(properties, key);
		return value === undefined ? undefined : value.trim() ? Number(value) : Number.NaN;
	};
	return { s7CpuType: raw === undefined ? "" : normalizeCpu(raw) ?? raw,
		s7Rack: number("Rack"), s7Slot: number("Slot") };
}

export function resolveS7Form(model: string | undefined, fields: S7FormFields) {
	const raw = fields.s7CpuType || model || "S1200";
	const cpu = normalizeCpu(raw);
	if (!cpu) throw new Error(`无法识别 S7 CPU“${raw}”，请在 CPU 类型中明确选择`);
	const bounded = (value: number | null | undefined, fallback: number, maximum: number, name: string) => {
		if (value == null) return fallback;
		if (!Number.isInteger(value) || value < 0 || value > maximum)
			throw new Error(`${name}必须是 0–${maximum} 的整数`);
		return value;
	};
	const rack = bounded(fields.s7Rack, 0, 7, "机架号");
	const slot = bounded(fields.s7Slot, cpu === "S300" ? 2 : cpu === "S400" ? 3 : 0, 31, "插槽号");
	if (["S200", "S200Smart"].includes(cpu) && (rack !== 0 || slot !== 0))
		throw new Error("当前 S7-200/200Smart 驱动使用固定连接地址，机架号和插槽号须为 0 或留空");
	return { cpu, rack, slot };
}

export function serializeS7Fields(model: string | undefined, fields: S7FormFields): Record<string, string> {
	const options = resolveS7Form(model, fields);
	const properties: Record<string, string> = {};
	if (fields.s7CpuType) properties.PlcType = options.cpu;
	if (fields.s7Rack != null) properties.Rack = String(options.rack);
	if (fields.s7Slot != null) properties.Slot = String(options.slot);
	return properties;
}
