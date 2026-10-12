<template>
    <el-button @click="visible = true">MQTT 转发状态</el-button>
    <el-dialog v-model="visible" title="MQTT 转发状态" width="720px" append-to-body>
        <div class="status-toolbar">
            <span>每 5 秒刷新 · {{ updatedAt ? `更新于 ${formatTime(updatedAt)}` : '正在读取状态' }}</span>
            <el-button :loading="loading" @click="refresh">刷新</el-button>
        </div>
        <el-alert v-if="fetchError" type="warning" :closable="false" show-icon
            title="转发状态读取失败" :description="`${fetchError}${overview ? '；下方为上次读取的状态。' : ''}`" />
        <el-card v-for="channel in channels" :key="channel.name" shadow="never" class="channel-card">
            <template #header>
                <div class="channel-header">
                    <strong>{{ channel.name }}</strong>
                    <el-tag v-if="channel.status" :type="connectionTagType(channel.status)">
                        {{ connectionText(channel.status) }}
                    </el-tag>
                </div>
            </template>
            <template v-if="channel.status">
                <div class="status-counts">
                    <el-statistic title="待发送（批）" :value="channel.status.pendingCount" />
                    <el-statistic title="发送成功（批）" :value="channel.status.sentCount" />
                    <el-statistic title="失败尝试（次）" :value="channel.status.failedCount" />
                </div>
                <p v-if="!channel.status.enabled && channel.status.pendingCount" class="status-note">
                    转发未启用，待发数据保留，启用后继续发送。
                </p>
                <p class="status-error">最近错误：{{ channel.status.lastError || '无' }}</p>
                <p class="status-note">最近尝试：{{ formatTime(channel.status.lastAttemptAt) }} ·
                    最近成功：{{ formatTime(channel.status.lastSentAt) }}</p>
                <p class="status-note">累计起始时间：{{ formatTime(channel.status.counterStartedAt) }}</p>
            </template>
            <el-alert v-else type="info" :closable="false" show-icon title="后台采集状态暂不可用"
                :description="overview?.hostError || '尚未获取后台采集服务的转发状态'" />
        </el-card>
        <p class="status-note">入队后仍需等待发送；发送成功及失败尝试按各服务本次启动累计，成功表示已发布至 MQTT Broker。</p>
        <template #footer><el-button @click="visible = false">关闭</el-button></template>
    </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { telemetryInfluxApi, type MqttForwardingOverview, type MqttForwardingStatus } from "@/api/telemetryInflux";

const visible = ref(false);
const loading = ref(false);
const overview = ref<MqttForwardingOverview | null>(null);
const fetchError = ref("");
const updatedAt = ref("");
const channels = computed(() => overview.value ? [
    { name: "页面采集", status: overview.value.gateway },
    { name: "后台采集", status: overview.value.host },
] : []);
let timer: ReturnType<typeof setTimeout> | undefined;
let controller: AbortController | undefined;
let requestVersion = 0;
let mounted = true;

function formatTime(value: string | null) {
    return value ? new Date(value).toLocaleString() : "暂无";
}

function connectionText(status: MqttForwardingStatus) {
    if (!status.enabled) return "未启用";
    if (status.isConnected) return "已连接";
    return status.pendingCount > 0 ? "等待连接，待发数据已保留" : "未连接";
}

function connectionTagType(status: MqttForwardingStatus) {
    if (!status.enabled) return "info";
    if (status.isConnected) return "success";
    return status.pendingCount > 0 ? "warning" : "info";
}

function stopPolling() {
    requestVersion += 1;
    clearTimeout(timer);
    timer = undefined;
    controller?.abort();
    controller = undefined;
    loading.value = false;
}

async function refresh() {
    if (!visible.value || loading.value || !mounted) return;
    clearTimeout(timer);
    loading.value = true;
    const version = requestVersion;
    controller = new AbortController();
    try {
        const result = await telemetryInfluxApi.getMqttStatus(controller.signal);
        if (version !== requestVersion || !mounted) return;
        overview.value = result;
        updatedAt.value = new Date().toISOString();
        fetchError.value = "";
    } catch (error: unknown) {
        if (version !== requestVersion || !mounted) return;
        const failure = error as { response?: { data?: { detail?: string; error?: string } }; message?: string };
        fetchError.value = failure.response?.data?.detail || failure.response?.data?.error || failure.message || "请稍后重试";
    } finally {
        if (version === requestVersion && mounted) {
            loading.value = false;
            controller = undefined;
            if (visible.value) timer = setTimeout(() => void refresh(), 5_000);
        }
    }
}

watch(visible, (open) => {
    stopPolling();
    if (open) void refresh();
});
onBeforeUnmount(() => {
    mounted = false;
    stopPolling();
});
</script>

<style scoped>
.status-toolbar, .channel-header, .status-counts { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
.status-toolbar { margin-bottom: 12px; color: var(--el-text-color-secondary); font-size: 13px; }
.channel-card { margin-top: 12px; }
.status-counts { flex-wrap: wrap; justify-content: flex-start; gap: 48px; }
.status-error { overflow-wrap: anywhere; font-size: 13px; }
.status-note { margin-bottom: 0; color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.6; }
</style>
