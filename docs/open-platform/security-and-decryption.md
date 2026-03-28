# 开放平台安全与加解密说明（当前轮次完整摘要）

## 1. 文件定位

本文件用于当前项目中所有涉及开放平台真实接口调用的安全、签名、加密、解密实现。

只要本轮代码会处理以下任一内容，就必须先阅读本文件并严格遵循：

- 请求签名
- 请求参数加密
- 响应 `data` 解密
- `version` 分支处理
- RTSP 地址响应解析

本文件不是提纲，必须作为可直接用于开发与验收的完整摘要。

---

## 2. 平台通信总规则

平台采用以下固定规则：

- 协议：`HTTPS`
- 请求方式：`POST`
- 请求体格式：`application/x-www-form-urlencoded`
- 响应格式：`JSON`
- 编码：`UTF-8`
- 正式环境域名：`https://vcp.21cn.com`
- 通用接口前缀：`https://vcp.21cn.com/open/token/`
- 请求头必须包含：`apiVersion=2.0`

说明：
- URL 中的参数名和参数值都应做 URL 编码。
- HTTP Body 中的参数值也应做 URL 编码。

---

## 3. 公共请求参数

平台要求把接口私有参数先加密成 `params`，然后与公共参数一起提交。

固定公共参数如下：

- `signature`
- `params`
- `appId`
- `version`
- `clientType`
- `timestamp`

### 3.1 字段说明

#### `signature`
签名算法固定为：

`signature = HMAC-SHA256(appId + clientType + params + timestamp + version, appSecret)`

要求：
- 字段顺序必须固定为：`appId + clientType + params + timestamp + version`
- 使用 `appSecret` 作为 HMAC-SHA256 密钥
- 不允许调整拼接顺序

#### `params`
`params` 固定为对所有私有请求参数进行 `XXTea` 加密后的结果。

规则：

`params = XXTea((参数1=值&参数2=值&...), AppSecret)`

要求：
- 私有请求参数按 `key=value&key=value...` 形式拼接
- 请求参数**无须排序**
- 使用 `AppSecret` 作为 XXTea 对称密钥
- 结果使用十六进制字符串形式传输

#### `appId`
平台分配给应用的接入 `AppId`。

#### `version`
服务端版本号，只允许以下两种：

- `v1.0`
- `1.1`

规则：
- `v1.0`：表示使用 XXTea 解密响应 `data`
- `1.1`：表示使用 RSA 私钥解密响应 `data`
- 平台默认使用 `1.1`
- 当使用 `1.1` 时，传入值必须是 `1.1`，**前缀不能写 `v`**

#### `clientType`
可选值：

- `0`：IOS
- `1`：Android
- `2`：Web/WAP/H5
- `3`：PC
- `4`：服务端

当前项目运行时应以本机配置文件中的真实值为准，不得硬编码猜测。

#### `timestamp`
当前 UTC 时间戳，单位为毫秒。

定义：
- 从 `1970-01-01 00:00:00 UTC` 到当前时刻的毫秒数

---

## 4. 请求头规则

所有相关业务接口请求头都必须包含：

- `apiVersion: 2.0`

如果缺少该请求头，平台会提示应用不存在或版本错误。

---

## 5. 请求加密规则

### 5.1 私有参数拼接

先把接口私有参数拼成普通查询串格式，例如：

`accessToken=xxx&enterpriseUser=xxx&deviceCode=xxx`

说明：
- 当前项目中常见私有参数包括：`accessToken`、`enterpriseUser`、`deviceCode`、`groupId` 等
- 私有参数内容来自接口自身文档，不得推断

### 5.2 XXTea 加密

请求侧固定使用 `XXTea` 对私有参数字符串加密。

规则：
- 明文字符集：`UTF-8`
- 密钥：`AppSecret`
- `AppSecret` 需要先转为十六进制字节串后参与 XXTea
- 输出结果为十六进制字符串

### 5.3 签名计算

在得到 `params` 后，按以下顺序拼接：

`appId + clientType + params + timestamp + version`

再使用 `appSecret` 做 `HMAC-SHA256` 计算，结果写入 `signature`。

### 5.4 最终提交格式

最终以 `application/x-www-form-urlencoded` 方式提交以下字段：

- `signature`
- `params`
- `appId`
- `version`
- `clientType`
- `timestamp`

---

## 6. 响应解密规则

这是当前轮次最关键的规则。

平台很多接口返回形如：

```json
{
  "code": 0,
  "msg": "成功",
  "data": "...密文..."
}
```

这里的 `data` **不能直接当 JSON 解析**。

必须先根据公共请求参数中的 `version` 做解密：

- 当 `version = v1.0` 时：`data` 使用 `AppSecret` 做 `XXTea` 解密
- 当 `version = 1.1` 时：`data` 使用本地 `RSA 私钥` 解密

解密完成后，得到的明文才是 JSON，再进入字段解析。

### 6.1 `version = v1.0`

使用：
- `AppSecret`
- `XXTeaDecrypt(cipherHex, "UTF-8", hex(AppSecret))`

### 6.2 `version = 1.1`

使用：
- 本地 `RSA 私钥`
- 对 `data` 密文字串做 `RSA 私钥解密`
- 解密结果应为 JSON 明文字符串

当前轮次针对 `getDeviceMediaUrlRtsp` 的已确认兼容约束：
- 真实日志已确认：RTSP 响应 `data` 可能返回为 Base64 密文字串，也可能返回为纯十六进制密文字串
- 当 `data` 仅包含 `0-9A-Fa-f` 且长度为偶数时，先按十六进制转字节，再进入 RSA 私钥解密
- 否则再按 Base64 转字节，再进入 RSA 私钥解密
- 若两者都不成立，必须按 `RTSP 响应解密失败` 分类，不得误归因为 padding 失败或 JSON 解析失败
- 该兼容仅用于把 RTSP 响应 `data` 转为 RSA 解密输入，不改变 `version=1.1` 使用 RSA 私钥解密的规则

### 6.3 当前项目直接约束

当前项目本机配置已使用 `Version = 1.1`。

因此当前轮次所有需要解密响应 `data` 的接口，都必须按以下规则处理：

- 先读取当前运行时 `version`
- 若值为 `1.1`
- 则用 **RSA 私钥** 解密 `data`
- 解密后再把明文 JSON 解析成对象

不允许：
- 跳过解密直接解析 `data`
- 把十六进制密文直接扔给 JSON 解析器
- 未确认 `version` 就猜测解密方式

---

## 7. 标准解密决策规则

实现中应遵循下列逻辑：

```text
如果 version 为空 -> 立即报错，不继续
如果 version == "1.1" -> 使用 RSA 私钥解密 data
否则 -> 使用 AppSecret 做 XXTea 解密 data
```

含义：
- `version` 是响应 `data` 解密方式的唯一分支依据
- 不能凭接口名称猜测
- 不能凭返回长度猜测
- 不能凭是否像 JSON 猜测

---

## 8. 当前轮次直接相关接口

### 8.1 设备状态查询

- 接口名：`getDeviceStatus`
- 路径：`/open/token/vpaas/device/getDeviceStatus`
- 完整 URL：`https://vcp.21cn.com/open/token/vpaas/device/getDeviceStatus`

当前轮次最小请求参数：
- `accessToken`
- `enterpriseUser`
- `deviceCode`

当前轮次关键响应含义：
- `status` / `onlineStatus` 一类字段表示设备在线状态

状态口径：
- `1`：在线
- `0`：离线
- `2`：休眠（普通休眠）
- `3`：休眠（保活）

### 8.2 RTSP 直播地址获取

- 接口名：`getDeviceMediaUrlRtsp`
- 路径：`/open/token/cloud/getDeviceMediaUrlRtsp`
- 完整 URL：`https://vcp.21cn.com/open/token/cloud/getDeviceMediaUrlRtsp`

当前轮次最小请求参数：
- `accessToken`
- `enterpriseUser`
- `deviceCode`
- `expire`（可选）
- `netType`（可选）

当前轮次关键响应字段：
- `url`
- `expireTime`

注意：
- RTSP 响应中的 `data` 是密文
- 必须先按 `version` 解密
- 在 `version = 1.1` 下，当前实现需兼容 Base64 与纯十六进制两种 RTSP 密文输入形态
- 解密后才会得到包含 `url` 和 `expireTime` 的明文 JSON

---

## 9. RTSP 当前实现的直接约束

对 `getDeviceMediaUrlRtsp` 的实现必须满足：

1. 先命中正确接口路径
2. 收到响应后先检查：
   - `code`
   - `msg`
   - `data`
3. 当 `code = 0` 且 `data` 存在时：
   - 不得直接把 `data` 当 JSON 解析
   - 必须先按 `version` 解密 `data`
4. 解密后得到明文 JSON，再提取：
   - `url`
   - `expireTime`
5. 如果解密失败：
   - 要给出明确中文错误分类
   - 不要把底层 JSON 解析器英文原始异常直接展示给业务用户

---

## 10. 日志与脱敏规则

必须继续记录：
- 接口名称
- 请求 URL
- 请求头
- 请求参数
- 响应结果
- 耗时
- 异常信息

但必须继续脱敏：
- `accessToken`
- `AppSecret`
- `RSA 私钥`
- RTSP URL 完整值
- 其他敏感凭证

规则：
- UI 可以显示完整 RTSP URL 供用户复制
- 日志中不得完整明文输出 RTSP URL
- 若打印响应原文，需要确保敏感字段先脱敏

---

## 11. 错误分类要求

本轮必须区分以下失败类型：

### 11.1 `accessToken` 问题
示例：
- token 获取失败
- token 失效
- token 缺失

### 11.2 状态接口问题
示例：
- 状态接口业务失败
- 状态接口返回错误码
- 状态接口响应结构异常

### 11.3 RTSP 接口问题
示例：
- RTSP 接口业务失败
- RTSP 接口返回错误码
- RTSP 接口返回密文但解密失败
- RTSP 接口解密成功但字段缺失

### 11.4 严禁的错误归因
不允许把以下情况误判为“接口已正确完成”：
- 命中错误路径后收到某种业务报错
- 收到密文后直接 JSON 解析失败
- 未做解密就声称字段不存在

---

## 12. 当前轮次禁止项

- 禁止推断接口路径
- 禁止推断字段名
- 禁止推断解密方式
- 禁止把密文直接当 JSON 解析
- 禁止因为当前只关心 `url` 就跳过完整解密步骤
- 禁止因“看起来像十六进制字符串”就自行改协议

---

## 13. 当前轮次验收口径

本文件相关实现通过的标准是：

1. 请求继续符合统一网关协议
2. `params` 继续按 XXTea 加密
3. `signature` 继续按 HMAC-SHA256 生成
4. `apiVersion=2.0` 请求头存在
5. RTSP 接口命中正确路径
6. RTSP 响应 `data` 先按 `version` 解密
7. 解密后成功解析出 `url` 和 `expireTime`，或至少给出准确的中文失败分类
8. 日志继续脱敏
9. 不破坏现有地图页、同步页、单点预览页

---

## 14. 本文件对应的原始文档来源

本摘要整理自以下原始文档：

- `通信协议和名词解析.docx`
- `消息加解密.docx`
- `API 调用说明.docx`
- `获取设备的RTSP直播链接.docx`

若本文件与原始文档冲突，必须以原始文档为准，并同步修正本文件。
