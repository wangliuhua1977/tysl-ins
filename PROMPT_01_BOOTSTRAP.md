# Codex 提示词 01：创建首版工程骨架

你正在一个全新的 Windows 11 桌面软件仓库中工作。请先读取仓库根目录 `AGENTS.md`，严格按其中约束执行；不要假设任何外部背景。

本次任务只做“首版工程骨架”，不要实现业务功能，不要发散到 AI、大屏、复杂报表、多渠道通知。

## 本次目标
创建一个可以启动、可编译、具备后续扩展基础的 `.NET 10 WPF` 解决方案骨架，为后续点位巡检系统开发做准备。

## 必做事项
1. 创建解决方案与项目结构：
   - `src/Desktop`
   - `src/Application`
   - `src/Domain`
   - `src/Infrastructure`
   - `src/Contracts`
   - `tests/Desktop.Tests`
   - `tests/Application.Tests`
2. 在 `src/Desktop` 建立 WPF 启动工程，采用 `Generic Host + DI + MVVM`。
3. 接入 WPF Fluent 主题基础能力，并预留 `System / Light / Dark` 三种主题切换入口。
4. 建立基础导航壳：
   - 运行总览
   - 点位地图
   - 异常处理中心
   - 工单处理
   - 点位详情
   - 基础配置
   页面先做空壳和占位，不做真实业务。
5. 建立日志基础设施：
   - 日志文件输出到项目根目录
   - 启动前清理旧日志
   - 区分应用日志与 API 日志两个文件
   - 先打通启动、窗口打开、页面切换日志
6. 建立配置体系：
   - `appsettings.json`
   - `appsettings.Development.json`
   - `Settings/*` 或等价目录
   - 所有敏感配置只保留占位符，禁止写真实值
7. 建立平台接入抽象，但先不联真实接口：
   - `ITokenService`
   - `IDeviceCatalogService`
   - `IDeviceStatusService`
   - `IMediaProbeService`
   - `IWorkOrderService`
   - `INotificationService`
8. 建立地图宿主基础：
   - 使用 `WebView2`
   - 先加载本地占位 HTML
   - 预留宿主与 JS 双向消息桥接口
   - 先不接高德真实地图 key
9. 建立本地存储骨架：
   - SQLite 配置占位
   - 故障、工单、点位维护信息、责任映射等聚合根/实体占位
   - 只建结构，不实现迁移和真实仓储逻辑
10. 建立 README：
   - 如何运行
   - 解决方案结构
   - 当前阶段只到工程骨架
   - 下一个建议任务

## 代码要求
- 代码必须可编译。
- 命名清晰，避免一次性生成过多无用文件。
- 不要引入未在 `AGENTS.md` 允许清单中的新依赖；若必须新增，先说明必要性。
- 不要写伪实现的业务逻辑；宁可抛 `NotImplementedException` 或返回明确占位结果。
- 不要生成大段说明文档，只更新必要 README。

## 验收要求
完成后请输出：
1. 修改文件列表。
2. 解决方案结构树。
3. 实际执行过的构建命令与结果。
4. 当前风险点。
5. 建议的下一条最小任务（只给 1 条）。
