# 第三方 MQTT 对接说明（MachineConnectionApi）

本文档用于第三方系统接入本项目的 MQTT 遥测数据。

## 1. 先理解调用方式

本服务对 MQTT 的角色是**发布者（Publisher）**，第三方系统通常作为**订阅者（Subscriber）**接收数据。

第三方若要拿到最新采集值，推荐按以下流程：

1. 先订阅 MQTT 主题。
2. 调用 HTTP 采集接口触发一次采集；采集批次持久化入队后，由后台发送 JSON。

## 2. MQTT 连接参数

当前默认配置（来自 `appsettings.json`）：

- Broker Host: `127.0.0.1`
- Broker Port: `1883`
- TLS: `false`
- Username: 空
- Password: 空
- QoS: `1`（AtLeastOnce）
- Retain: `false`

> 如部署环境不同，请以运行环境中的 `Mqtt` 配置为准。

## 3. Topic 规则

- Topic 前缀：`machines/telemetry`
- 最终 Topic：`machines/telemetry/{deviceId}`

说明：

- `{deviceId}` 来自采集请求中的 `deviceId`。
- 若 `deviceId` 包含 `/ \ + # 空格 Tab`，会被替换为 `_`。

建议第三方先使用通配订阅：

```bash
mosquitto_sub -h 127.0.0.1 -p 1883 -t "machines/telemetry/#" -v
```

## 4. 如何触发发送（HTTP 触发）

本服务在执行采集接口后发布 MQTT 消息。

- 方法：`GET`
- 地址：`/api/datacollection/collect?deviceId=<设备ID>`

示例：

```bash
curl "http://<api-host>:<api-port>/api/datacollection/collect?deviceId=test3"
```

采集响应中的 `mqttQueued=true` 表示已经持久化入队，`mqttMessageId` 是本批次稳定 ID；不表示已经送达。`mqttError` 表示入队失败，`storageError` 表示历史存储失败，两者独立。

页面采集使用 `POST /api/telemetry/influx/batch`，响应包含：

- `written`、`storageEnabled`、`storageError`：历史存储结果。
- `mqttEnabled`、`mqttQueued`、`mqttMessageId`、`mqttError`：MQTT 入队结果。
- 兼容字段 `mqttPublished` 固定为 `false`，实际发送结果从状态接口读取。
- 历史库失败但 MQTT 入队成功，或 MQTT 入队失败但历史库成功，返回 HTTP 200 并携带各自错误；均失败返回 HTTP 502。空批次或全部禁用返回 `skipped=true`。

`GET /api/telemetry/mqtt/status` 返回 `gateway` 和 `host` 两个通道状态，需登录并具有数据读取、写入、设备管理或报告权限之一。Host 不可访问时保留网关状态，`host=null` 并返回 `hostError`。

状态包含 `enabled`、`isConnected`、`pendingCount`、`sentCount`、`failedCount`、`lastError`、`lastAttemptAt`、`lastSentAt`、`counterStartedAt`。数量以批次计；`failedCount` 是失败尝试次数，成功与失败累计从本次进程启动开始。页面“MQTT 转发状态”可查看两条通道。

## 5. 消息体格式（JSON）

消息体结构：

```json
{
  "messageId": "af6100a0b0aa43799403b189a0507012",
  "deviceId": "test3",
  "collectedAt": "2026-04-21T06:10:30.123+00:00",
  "points": [
    {
      "name": "Temperature",
      "path": "ns=2;s=Channel1.Device1.Temp",
      "dataType": "Double",
      "value": 26.4,
      "quality": "Good",
      "timestamp": "2026-04-21T06:10:30.000Z",
      "status": "成功",
      "errorMessage": null
    }
  ]
}
```

字段说明：

- `messageId`: 本批次唯一 ID，重试及服务重启补发保持不变；接收方按此去重。
- `deviceId`: 设备唯一标识
- `collectedAt`: 本批次采集时间（服务端时间）
- `points`: 点位数组
  - `name`: 点位名称
  - `path`: 点位路径
  - `dataType`: 数据类型
  - `value`: 点位值（数字、布尔或字符串）；null 值的字段会省略。
  - `quality`: 质量
  - `timestamp`: 点位原始时间戳（若上游提供）
  - `status`: 采集状态（如 `成功`、`失败`）
  - `errorMessage`: 失败原因；null 时省略。

## 6. 第三方最小联调步骤

1. 启动 MQTT Broker（例如 Mosquitto）。
2. 启动 MachineConnectionApi。
3. 在第三方侧订阅 `machines/telemetry/#`。
4. 调用采集接口：`GET /api/datacollection/collect?deviceId=test3`。
5. 验证第三方收到 `machines/telemetry/test3` 主题消息。

## 7. 常见问题

- 收不到消息：
  - 检查 Broker 地址和端口是否一致（`127.0.0.1:1883`）。
  - 检查是否订阅了正确主题（建议先用 `machines/telemetry/#`）。
  - 检查 `Mqtt:Enabled` 是否为 `true`。
  - 检查是否真的触发了采集接口。
- 能订阅到手工测试消息，但收不到业务消息：
  - 优先检查采集接口是否成功返回，以及设备是否有已配置采集点。

## 8. 故障恢复与部署

MQTT 批次先写临时文件、刷新磁盘，再原子移入待发目录；仅在 Broker 确认发布成功后删除。Broker 不在线时保留消息，后台自动重试；服务重启后继续发送目录里的待发消息，无需再次触发采集。

- 网关目录配置：`Mqtt:OutboxDirectory`。
- Host 目录配置：`DataOutput:Mqtt:OutboxDirectory`。
- 默认均为各自程序输出目录下的 `App_Data/mqtt-outbox`；两个服务必须使用不同目录。生产部署宜配置固定绝对路径，升级时保留，目录配置修改后重启生效。
- 磁盘写入失败会明确返回入队失败；损坏的待发文件保留并显示错误。停用 MQTT 时不接收新批次，已有待发消息保留，重新启用后继续发送。
- 网关支持 `Username`、`Password`、`UseTls`；`QualityOfService=0` 自动提升为 1，以等待 Broker 确认。连接和发送超时分别由 `ConnectTimeoutSeconds`、`PublishTimeoutSeconds` 配置。
- Host 原主题 `industrial-iot/devices/{deviceId}/data` 及原字段保持，新增 `messageId`，同样使用持久化队列。

成功表示 Broker 已确认，不是第三方业务已经处理。确认后、删除待发文件前若进程退出，可能重复投递，接收方应按 `messageId` 去重。Broker 应配置持久化；需要在订阅者断线期间保留消息时，订阅者应使用固定 ClientId、持久会话和 QoS 1/2。当前实现不提供历史数据库自动重放，也不保证不同批次的发送顺序。
