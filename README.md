# CSUMCL Backend

CSUMCL 启动器的 ASP.NET Core 资源站与管理后台，目标框架为 .NET 9。此仓库仅包含后端源码、测试及通用部署模板；客户端源码位于 https://github.com/yuhui258036/CMU-MCL。

文中的域名、端口、目录和部署拓扑均为配置示例或程序默认值，不代表任何运行中服务器的配置。部署时请替换为自己的环境参数。

## 功能

- 公开 HTTPS 静态资源站、仅绑定回环地址的管理后台。
- 管理员密码 PBKDF2 验证、会话与 CSRF 检查。
- 整合包上传进度界面、revision 管理、签名 manifest。
- 2 MiB 分块、默认 2 路浏览器并发、逐块及整包 SHA-256 校验、暂停/续传、后台发布任务恢复。
- 从 .mrpack 生成增量文件索引与 overrides 哈希。
- 上传时提取 Minecraft / 加载器元数据，启动时回填已有记录。
- Range 下载、整合包长期缓存及元数据缓存控制。

客户端自动更新的公开 ZIP 和签名清单由静态资源目录提供；本仓库不包含生产更新包或生产签名材料。

## 构建与测试

### 中文管理界面

后台登录、整合包概览、新建/编辑和上传页面参考 https://www.yeqing.net/status/ 布局，采用紧凑侧栏、中性色卡片和简短文案。左下角可切换亮暗主题，选择保存在当前浏览器；主题切换不会清空表单。社区图标复用客户端素材。页面资源随程序集嵌入，不需要 Node 构建或外部字体/CDN。

上传页区分文件校验、文件上传、校验与发布和发布完成四个阶段，支持拖入文件和点击选择。暂停保留传输进度的显示值，继续时未完成的分块会重传，进度达到暂停前位置后继续增长。服务器处理阶段显示不确定进度，不将上传 100% 当作发布成功；此时不能暂停服务器任务，刷新后会恢复查询。失败任务有中文提示及确认取消入口；发布失败需取消后重新上传。续传拒绝不同文件，连续发布会使用最新 revision。

后台内导航与表单保存使用局部页面切换，上传任务保留在当前文档。任务入口参考 MCSManager 的小型上传图标、数量角标和悬停进度提示，放在侧栏工具区，默认收起。点击后展开独立任务面板：宽屏占用右侧空间，窄屏占用底部空间，不覆盖正文；收起不会暂停上传。面板支持查看、暂停、继续和取消。所选文件缓存在当前浏览器同源 IndexedDB；刷新或重新打开后台后，正在上传的任务自动续传，手动暂停的任务保持暂停。浏览器完全关闭期间无法继续发送数据。缓存受浏览器可用空间和清理策略限制，保存失败时显示提示并在离开前提醒，旧任务或缓存被清理后仍可重新选择原文件续传；发布完成后删除文件缓存；取消上传会停止发送并清理服务器临时数据，同时保留所选文件及浏览器文件缓存，刷新或切页后仍可直接重新上传。取消不会保留已传进度，需要续传时使用暂停。Web Locks 防止同一浏览器多个标签页同时运行同一整合包的上传。

后台文案修改表见 [后台文案清单.md](后台文案清单.md)。MCSManager 交互参考：[UploadBubble.vue](https://github.com/MCSManager/MCSManager/blob/master/frontend/src/components/UploadBubble.vue)。

建议在隔离测试环境中验收亮暗主题、窄屏、暂停/刷新续传、同页连续发布、损坏包失败与取消清理，使用临时账号、密钥和合成包。`HttpUploadTests` 无论通过 `dotnet run` 还是 `dotnet <测试程序集>` 执行，都通过 dotnet 主机启动临时后端，避免重复启动测试程序。

资源清单 `/admin/manifest` 默认显示已发布资源表格；`/admin/manifest?format=json` 保留原始 JSON，公开下载端的 `/manifest.json` 和签名协议不变。

### 命令

安装支持构建 `net9.0` 的 .NET SDK，并为运行与测试准备兼容的 ASP.NET Core 运行时。

```sh
dotnet build PclCmu.Admin.csproj -c Release
dotnet run --project tests/PclCmu.Admin.Tests -c Release
node tests/PclCmu.Admin.Tests/hash-worker.test.cjs
node tests/PclCmu.Admin.Tests/upload-ui.test.cjs
```

测试在临时目录创建合成整合包及临时签名密钥，完成后清理；不需要真实服务器数据、真实账号或生产私钥。
HTTP 测试启动两个临时回环监听器，验证真实请求、公开/后台隔离、CORS、重启续传、签名和 Range；不连接生产源站或 CDN。浏览器哈希测试包含跨 512 MiB 位数进位的流式输入。

可选增强验收：

```sh
# 64 MiB 合成包、1/2/4 路并发，记录上传写盘/校验时间及进程峰值内存
dotnet run --project tests/PclCmu.Admin.Tests -c Release -- --performance
# 需已安装 Playwright 与匹配的 Chromium；也可用 PLAYWRIGHT_MODULE 指向已有模块
dotnet run --project tests/PclCmu.Admin.Tests -c Release -- --browser
```

`--browser` 使用全新的无头 Chromium 上下文、临时管理员及文件，实际运行上传页面、Worker 和 XHR；经过另一个本地 HTTPS 代理端口，检查真实 OPTIONS/CORS、丢失成功响应后的自动重试、暂停、刷新、跳过已确认分块及发布文件一致性。测试不拦截浏览器网络请求；结束后关闭浏览器和测试服务。测试代理使用一次性自签证书，仅该浏览器上下文接受此证书，不修改系统信任或生产证书。可设置 `CSUMCL_BROWSER_ARTIFACTS` 保存暂停/完成截图。代理的请求体上限为 64 KiB，测试包为 2 MiB，以验证每次请求受限时的分块流转；生产默认块仍为 2 MiB。

Linux 可使用 `tests/PclCmu.Admin.Tests/run-linux.sh`：将源码与已还原的 NuGet 缓存分别只读挂载到 `/source`、`/packages`，为 `/work`、`/tmp` 提供可写临时文件系统，设置 `NUGET_PACKAGES=/packages`、`DOTNET_CLI_HOME=/tmp/cli`，在兼容的 .NET SDK 容器中执行该脚本。需要允许跨主版本运行时滚动时，可设置 `DOTNET_ROLL_FORWARD=LatestMajor`。建议使用无主机端口映射的隔离容器，并按测试需求设置资源限额。脚本准备私有 `XDG_RUNTIME_DIR`，兼容只读容器中的 .NET 命名锁。

还可直接验证已编译程序集与计划部署的运行时是否兼容。例如，将 `tests/PclCmu.Admin.Tests/bin/Release/net9.0` 只读挂载为 `/tests`、测试脚本目录挂载为 `/scripts`，分别为 `/tmp`、`/published` 提供两个独立 tmpfs，在目标 ASP.NET Core 运行时容器中运行 `sh /scripts/run-runtime-linux.sh --performance`。脚本打印实际运行时版本；跨文件系统测试通过 `stat` 确认设备号不同，验证复制中断、残缺目标重试及损坏目标拒绝发布。

性能输出以 `PERFORMANCE` 开头。它测量测试环境中的存储和哈希管线，不包含浏览器哈希、真实公网/CDN 时延；如果测试目录使用 tmpfs，不能将其吞吐量与磁盘存储直接比较。`processPeakWorkingSetMiB` 是累计进程峰值，包含测试框架，不代表单任务独占内存。

## 分块上传与 CDN 回源

管理后台绑定回环地址，可通过 SSH 隧道访问。选择 `.mrpack` 后，浏览器 Worker 以有界内存计算整包和分块哈希，再创建任务、上传缺少的块、提交异步校验并轮询发布结果。刷新后优先恢复浏览器缓存文件；缓存不可用时重新选择同一文件即可续传。已进入校验队列的任务无需重新选择文件，页面会恢复查询。重启使管理员登录失效，但不会清除上传任务或分块，重新登录后继续。

### 传输方式

CDN 上传入口目前通过后端 `appsettings.json` 配置，管理页面没有单独的设置开关。默认 `ChunkUpload.PublicIngressEnabled=false`，文件数据通过私有后台入口上传；开启后，上传页自动使用后端返回的 CDN 地址，无需管理员在页面上另选上传方式。

```text
文件数据：浏览器 → EdgeOne 上传域名 → 源站公开上传接口 → VPS 磁盘
任务管理：浏览器 → SSH 隧道 → 私有后台（创建、查询、续期、取消、发布）
```

EdgeOne 负责转发上传请求，文件最终存放在 VPS，不是上传到 CDN 的永久存储。`PublicUploadBaseUrl` 控制上传地址，`PublicBaseUrl` 控制发布清单中的资源下载地址，两者可以使用不同域名。上传接口不缓存；已有下载资源的缓存规则保持独立。

### 1. 配置后端

先备份部署环境的 `appsettings.json`，将以下字段合并到现有 `ChunkUpload` 配置中，不要覆盖已有的数据路径、密钥、证书和监听器配置。以下仅为示例：上传域名为 `upload.example.com`，SSH 隧道本地端口为 `18080`，浏览器访问 `http://127.0.0.1:18080/admin`：

```json
"ChunkUpload": {
  "PublicIngressEnabled": true,
  "PublicUploadBaseUrl": "https://upload.example.com",
  "AllowedOrigins": ["http://127.0.0.1:18080"],
  "ChunkSizeBytes": 2097152,
  "QuotaBytes": 8589934592,
  "MinFreeBytes": 2147483648,
  "MaxConcurrentChunks": 4,
  "IdleHours": 24,
  "TokenMinutes": 60
}
```

上面是 `appsettings.json` 内的配置片段，不是完整配置文件。

| 字段 | 填写方式 |
| --- | --- |
| `PublicIngressEnabled` | `true` 开启公开分块上传入口；默认 `false` |
| `PublicUploadBaseUrl` | 已接入 EdgeOne 的 HTTPS 上传域名，不附带 API 路径、查询参数或凭据 |
| `AllowedOrigins` | 打开管理后台的来源，即协议、主机和浏览器使用的端口；不带 `/admin` 或末尾斜杠，不填写上传域名代替后台来源 |
| `ChunkSizeBytes` | 单次上传请求的数据大小，默认 `2097152`（2 MiB），支持 64 KiB–8 MiB；须不超过 CDN 和源站反向代理的单请求限制 |
| `QuotaBytes` / `MinFreeBytes` | 上传任务总磁盘预算和需保留的可用空间，按源站磁盘容量调整 |

`AllowedOrigins` 使用精确匹配。示例中的 `http://localhost:18080` 与 `http://127.0.0.1:18080` 是不同来源；若两者都使用，分别加入数组。本地隧道端口可以与远端后台端口不同；例如将本地 `18080` 转发到服务端示例监听地址 `127.0.0.1:9443`，允许来源填写浏览器使用的 `18080`。仅支持 HTTPS 来源或回环地址的 HTTP 来源，不支持 `*`。

`DataDir` 必须位于 `WwwRoot` 外。配置在服务启动时读取，完成下述 CDN 配置后，在维护窗口重启后端并重新登录、刷新上传页；现有任务和已确认分块保留。

### 2. 配置 EdgeOne 与源站

1. 将上传域名接入 EdgeOne 并开启代理加速，按控制台要求完成 DNS 接入，为浏览器访问配置有效的 HTTPS 证书。
2. 源站填写 VPS 的直连 IP 或独立回源域名，回源到公开服务入口。仓库示例为 HTTPS 443；若使用 Nginx 等反向代理，以实际公开监听端口为准。回源 Host、SNI 和源站证书应与源站部署相匹配，不要使用会再次绕回 EdgeOne 的加速域名作为回源目标。
3. 源站反向代理需将下表中的上传路径完整转发到后端公开监听器。不要转发到私有管理端口，也不要将 `/admin` 或 `/admin/api` 暴露给 CDN。
4. 为上传域名下的 `/api/uploads/*/chunks/*` 配置以下规则，避免套用下载资源的长期缓存规则。

| 项目 | 配置要求 |
| --- | --- |
| 请求方法 | 允许 `PUT`、`OPTIONS` 到达源站；EdgeOne 和源站反向代理都不能拦截 |
| 请求路径 | 保持原路径，不删除 `/api/uploads` 前缀或重写任务 ID、分块序号 |
| 缓存 | 不缓存该路径的响应，源站返回 `Cache-Control: no-store`；包括错误响应和预检响应 |
| 请求头 | 保留 `Authorization`、`Origin`、`Content-Type`、`X-Chunk-SHA256`，以及预检用的 `Access-Control-Request-Method`、`Access-Control-Request-Headers` |
| 跨域响应 | 保留后端返回的 `Access-Control-Allow-*` 和 `Vary: Origin`；不要在 EdgeOne 或反向代理重复添加同名 CORS 头 |
| 请求体与超时 | 允许至少配置的单块大小（默认 2 MiB），回源超时须足够完成单块传输；若前面还有反向代理，需同步检查其请求体上限和超时 |
| 安全规则 | 针对上传 API 调整误拦截或交互式验证规则，避免返回 HTML 验证页面；不需要关闭整个站点的防护 |

官方依据：[EdgeOne HTTP 方法限制](https://cloud.tencent.com/document/product/1552/107286)包含 PUT/OPTIONS；[CORS 配置说明](https://edgeone.ai/document/71623)说明源站与边缘重复添加 CORS 头会导致错误；[缓存规则](https://edgeone.ai/document/54213)说明不缓存策略。具体控制台可用选项、套餐上传限制、WAF 与域名配置以实际账户为准；本地测试通过不代表已通过真实 EdgeOne 验收。

### 3. 验证接入

使用独立测试整合包验证，避免将验收文件发布到正式整合包中。

1. 打开后台上传页，选择文件并开始上传。在浏览器开发者工具的网络面板中确认块请求发往 `https://upload.example.com/api/uploads/{任务ID}/chunks/{序号}`，而不是本地后台地址。
2. 跨域预检 `OPTIONS` 和成功的 `PUT` 应返回 `204`。预检响应中的 `Access-Control-Allow-Origin` 必须与后台页面来源一致；已命中浏览器预检缓存时不一定每次都出现 OPTIONS。
3. 确认创建、查询、续期、取消和发布仍通过私有后台的 `/admin/api/uploads` 请求完成。上传域名仅开放写块入口，不提供这些管理操作。
4. 测试暂停、切换后台页面、继续和取消；另开一次测试上传直到“发布完成”，核对发布清单、文件签名和下载内容。上传进度 100% 不等同于发布成功。
5. 结合 EdgeOne 请求日志和源站访问日志确认请求经过边缘回源。仅看到 CDN 域名不能证明实际路由或上传速度已经改善。

| 现象 | 优先检查 |
| --- | --- |
| 浏览器报告跨域错误 | `AllowedOrigins` 的协议、主机和本地端口；预检是否被拦截；是否有重复的 CORS 响应头 |
| `404` | 公开入口是否开启、回源是否到正确的公开监听器、路径是否被重写 |
| `401` / `403` | 上传令牌及相关请求头是否完整转发；允许来源及 EdgeOne 安全日志 |
| `413` | CDN 与源站反向代理的单请求大小限制；分块大小是否超限 |
| `502` / `504` | 源站连通性、监听端口、TLS/Host/SNI 配置与回源超时 |
| 上传接口返回 HTML | 是否跳转到登录页，或被边缘交互式验证、错误页替换 |

若需停用 CDN 上传，待活动任务暂停或结束后，将 `PublicIngressEnabled` 改回 `false`，重启后端并重新登录、刷新上传页，文件请求将恢复走私有后台入口。不要为此删除任务数据或改动已有下载域名。

### 接口与任务限制

| 路径 | 方法 | 权限与用途 |
| --- | --- | --- |
| `/admin/api/uploads` | POST | 管理员会话 + `X-CSRF-Token`；创建/找回同一文件任务 |
| `/admin/api/uploads/{id}` | GET / DELETE | 登录后查询；删除另需 CSRF，仅取消未发布任务 |
| `/admin/api/uploads/{id}/renew` | POST | 登录 + CSRF，签发此任务的临时上传令牌 |
| `/admin/api/uploads/{id}/complete` | POST | 登录 + CSRF，202 异步校验，重复提交返回同一结果 |
| `/api/uploads/{id}/chunks/{index}` | PUT / OPTIONS | 可选公开入口；仅写块，不能创建/查询/发布任务 |

块请求使用 `Authorization: Bearer <临时令牌>`、`Content-Type: application/octet-stream`、`X-Chunk-SHA256`，长度必须与该块完全一致。同一块相同哈希重复请求幂等，不同哈希返回 409。令牌只在内存传递，服务器仅保存哈希；不放入 URL。默认 60 分钟到期，可通过已登录后台续期。任务归属于此单管理员后台，不绑定易失的登录会话 ID。

磁盘预算按每个未完成/失败任务最多两倍原文件大小预留（块数据 + 发布暂存文件），总额默认 8 GiB，并保留 2 GiB 可用空间。失败任务仍计入预算；取消立即清理块，默认闲置 24 小时过期。单文件仍受 `UploadLimitBytes` 限制，分块不会解除整包大小限制。默认最多 100 个任务、全局 4 个同时写入块、1 个昂贵校验任务。

100 个任务上限包括保留的完成/取消回执，直到过期清理；取消释放块空间，不立即删除用于幂等响应的任务记录。清理失败留下的分块仍计入磁盘预算。

## 发布一致性与恢复

旧 multipart 上传和新分块上传共用 `PackPublishService`。版本检查和提交与实例元数据修改共享写锁；已发布的版本文件不覆盖、不因输掉并发竞争而删除。公开文件准备好后才切换清单。签名在提交前生成，`data/publication.pending.json` 记录提交内容，进程中断后启动时恢复。恢复尚未完成时服务返回 503，资源会保留，不继续接受新发布。不要手动删除此日志或 `data/publishing` 后再启动旧版本。

这是单进程文件存储，启动持有 `data/writer.lock` 的独占文件句柄，禁止第二个写入进程。`instances.json` 损坏会拒绝启动，避免自动写回空实例列表。测试覆盖进程/写入异常恢复；不宣称覆盖操作系统或存储设备突然断电的全部持久性语义。

任务状态暂时写盘失败会保留作业并重试，同时暂缓新的上传写入，不因此停止公开下载服务；若发布已经完成但回执尚未写成，会核对不可变 revision，避免重复发布。提交日志写成后的资源故障则需要修复存储并重启恢复。跨磁盘放置资源时先复制到目标目录内不公开提供的临时扩展名，再在目标文件系统改名；启动完成日志恢复后清理仅由程序生成的 GUID 暂存包。临时文件删除失败也不会占住校验/写入工作槽。

清单与签名仍是两个 URL，CDN 缓存或发布瞬间可能不同步；新版客户端会有限次数刷新成对请求并再次验签，始终拒绝验签失败的数据。`/packs/` 和带版本号的 `/update/windows-x86_64/<version>.zip` 为长期 immutable 缓存；发布时不得重用这些路径覆盖字节。清单、签名与 latest.json 保持重新验证，上传 API 为 `no-store`。

升级前备份外部 data、wwwroot、配置和现有程序；暂停接收新任务、完成或取消正在发布的任务，确认没有 pending 日志后才切换版本。保留现有密钥与历史资源。回滚前先由新版本完成日志恢复，不能用旧程序跳过未完成提交。

## 配置

后台整合包管理使用独立卡片，左侧手柄支持拖拽或方向键上下排序，自动保存后同步生成签名清单。新建整合包排在末尾，编辑元数据保留现有排序；页面列表过期时返回冲突，需要刷新后重新操作。

新建与编辑支持图标网址或文件上传，上传文件优先。网址仅保存 HTTP/HTTPS 引用，服务器不主动抓取。文件支持 PNG/JPG，最大 2 MiB、4096 × 4096 像素，检查格式头与尺寸后保存到 `WwwRoot/icons/<sha256>.png|jpg`，公开入口按图片类型提供并长期缓存。替换图标使用新 URL，旧图标保留以兼容已有缓存；备份资源目录时包含 `icons/`。若额外配置了 Nginx/CDN 路径白名单，需允许 `/icons/` 静态资源，管理和上传表单仍只在管理入口开放。

将 appsettings.example.json 复制为本机 appsettings.json，并填写实际公开域名、证书路径和外部数据路径。该本机文件已被 Git 忽略。示例中的 example.com 不是可直接使用的部署地址。

管理员密码哈希通过 `PCL_CMU_ADMIN_PASSWORD_HASH` 环境变量传入。服务模板中的环境文件路径 `/opt/pcl-cmu/admin.env` 仅为示例，应替换为部署环境中的外部配置文件路径。签名私钥和 TLS 私钥由部署者独立配置到仓库之外；不要将密钥内容写进 appsettings 或源码。

配置模板以 HTTPS 443 为公开端口、`127.0.0.1:9443` 为管理监听地址。端口可按部署环境调整，但管理监听器必须绑定回环地址，且 `AdminPort` 与管理监听端口一致。整合包和实例状态分别存放在 `WwwRoot`、`DataDir` 指定的外部目录，升级前单独备份，并保留现有实例和 revision。

## 部署模板

```sh
dotnet publish PclCmu.Admin.csproj -c Release -r linux-x64 --self-contained true -o publish
```

部署前准备有效 appsettings.json、管理员环境配置、签名密钥和证书。`deploy/pcl-cmu.service` 中的 `/opt/dotnet/dotnet` 和 `/opt/pcl-cmu/` 均为示例路径，应按运行时安装位置和应用目录调整。`deploy/install.sh` 是需审核后使用的安装模板，会复制应用并重启服务；其中自动生成的 example.com 自签证书仅供开发示例，生产请预先配置受信任证书。

不要用空数据目录覆盖现有服务器数据。仓库不包含生产 data、wwwroot、证书、私钥、密码哈希、日志、构建产物或实际服务器地址。上传此源码仓库不会执行线上部署。

## 提交范围与敏感信息

.gitignore 采用源码白名单并额外排除密钥、环境文件和运行目录；项目发布规则也排除证书、密钥及凭据文件。只提交审核过的源码和示例。新增配置项时请同步检查是否含密码、令牌、连接字符串或私钥；不要使用 git add -f 绕过排除规则。

参见 SECURITY.md。
