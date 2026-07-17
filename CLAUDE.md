# CLAUDE.md — ILRuntime

ILRuntime：纯 C# IL 解释运行时（Ourpalm，MIT）。运行时加载 .NET DLL（CIL），用内置寄存器 VM（CIL→`OpCodeR` 的 JIT）解释执行，用于 Unity/iOS 等无 JIT 平台的 C# 热更新。程序集解析依赖内部分支 Mono.Cecil。

## 当前分支状态（动手前必读）
分支 `features/object-model-overhaul` 正做 **Neo 重构**：新对象模型（`byte[] Primitives + AutoList ManagedObjects`）+ 新解释器 `ExecuteNeo`（`byte*` 紧凑帧）。代码大量使用条件编译：

- `ENABLE_NEO_MODE`：开 = Neo（新模型 + `ExecuteNeo`），关 = Legacy（`StackObject[]` + `ExecuteR`）。
- `USE_OLD_OBJ_MODEL`：旧宏，待清理，语义与 `!ENABLE_NEO_MODE` 重叠。
- **两种对象模型不可混用**（值类型字段访问会格式转换 → 性能退化）。
- Neo 按 26 步推进：**Step 1-26 主干已全部完成**（19 委托 / 20 async / 22 泛型模板 / 23 NeoAssembly / 24 ilrt_neoc / 25 加载器+Cecil 解耦 / 26 性能验证均归档；21「JIT 完整改造」横切贯穿各步），外加大量派生/边角 change（[OPT-HARDEN]、13b、[CATCH-COMPLETE]、completion-portfolio、neo-overhaul 等）。**全量 Neo 冒烟已归零**（951 ran / 0 failed；overhaul wave-2 的 64 个 child 把 189 个失败修到 0）。NeoStep 417/0。`ExecuteNeo` 里的 `NotImplementedException` 现在几乎为**零**（189 全量失败里只有 2 个是 NIE，其余全是真运行时 bug，已全部修复）。overhaul 交接见 `rasen/changes/neo-overhaul/handoff/lead-15.md`；主干 26 步进度见 `.trae/documents/neo-handoff.md` 与 `neo-implementation-steps.md`。

改执行 / 对象模型前先读 `.trae/documents/`（见下）。深度架构 / 模块 / API 详解作为技能按需加载：`.claude/skills/ilruntime/`（入口 `SKILL.md`）。

## Neo 实施计划与任务流（.trae）
整体计划在 `.trae/`，三层目标：Legacy（默认，向后兼容）/ Neo JIT（**主干 Step 1-21 已完成**，运行时编译）/ Neo AOT（`ilrt_neoc` 预编译 `.neo`，**Step 22-26 已完成**，纯优化层）。三层均已落地，当前在 overhaul 阶段做边角完善。

**计划文档**（`.trae/documents/`）：
- `neo-handoff.md` — **接手工作前先读**：环境/构建测试命令、当前进度、工作流、代码坑、待办的完整交接。
- `neo-implementation-steps.md` — **26 步路线图 + 依赖图 + 建议顺序**，选「下一步」的入口。
- `neo-deferred-items.md` — **推迟项解决映射**（各步推迟的内容 + 落到哪个后续步骤；已解决项归档于此）。
- `object-model-design.md` — 新对象模型已实现部分（字段布局 / `byte[]+AutoList` / 特化 Ldfld·Stfld）。
- `object-model-neo-design.md` — Neo 解释器设计（Call 约定 / VTable / 异常 / Box / async 等已敲定方案）。
- **durable 能力规格**在 `openspec/specs/<capability>/spec.md`（11 个：neo-dispatch/value-types/boxing/exceptions/type-checks/arrays/byref/optimizer/newobj/**async/debugger**）；各 step 的 proposal/design/tasks/review/ship-log 归档在 `openspec/changes/archive/`（Step 11-26，跨 2026-07-04 ~ 07-11 多日）。

**规格存放（两套，分界在 Step 10/11）**：
- **Step 1-10 归档**：`.trae/specs/implement-neo-step*/{spec,checklist,tasks}.md`（openspec 格式，历史产物，**不再新增**）。
- **Step 11+ 走 openspec 流程**：变更提案放 `openspec/changes/<change>/`（`proposal.md` + `specs/<capability>/spec.md` 的 `ADDED`/`MODIFIED`/`REMOVED` delta + `tasks.md`）；完成后归档——delta 合并进 `openspec/specs/<capability>/spec.md`，change 目录移入 `openspec/changes/archive/`。
- **识别「下一个待做项」**：26 步主干均已归档（Step 1-10 在 `.trae/specs/`，11-26 在 `openspec/changes/archive/`），**已无「下一个 step」**。当前待办 = overhaul 边角 bug 与未覆盖指令形状，见 `rasen/changes/neo-overhaul/handoff/lead-13.md` 的 surfacedFollowups（历史推迟项见 `.trae/documents/neo-deferred-items.md`）。

**任务流**：`neo-implementation-steps.md` 选步 → 读该步 `spec.md` → 改代码 → 加 `TestCases/NeoStep<N>Test.cs` → `Debug_Neo` 跑该步 + 回归前序 step（见下文「测试」）。规则见 `.trae/rules/unittest_guide.md`。

## 构建（关键坑：sln 不能整体构建）
`ILRuntime.sln` 整体构建失败：`Debugging/VS2022/ILRuntimeDebuggerLauncher2022`（net472 VSIX 扩展）无法消费本分支的 `netstandard2.1`（NU1201）。这是分支的有意改动，VS 调试器扩展与 Neo 开发无关。**只构建开发子集**：

```bash
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo   # 传递构建 ILRuntime / ILRuntimeTestBase / LitJson，0 错误
dotnet build TestCases/TestCases.csproj -c Debug                       # → TestCases/bin/Debug/netstandard2.1/TestCases.dll
```

仓库根 `nuget.config` 用 `<clear/>` + nuget.org 绕过本机失效的机器级 NuGet 源（`N:\...\Shared\NuGetPackages` 旧 VS 残留，NU1301）。删该文件即还原默认。`dotnet` 8.0.400 可用；msbuild 不在 PATH（不影响 dotnet 构建）。

## 测试（自研反射框架，非 xUnit）
用例 = `TestCases/` 的 **public static 无参方法**（可标 `[ILRuntimeTest]`），编译成 DLL 由解释器跑。CLI 用法：`ILRuntimeTestCLI <TestCases.dll> <HotfixAOT.patch> <useRegister:true|false> [名称过滤]`。CLI 多目标（netcoreapp3.0 + net8.0），`dotnet run` **必须加 `-f net8.0`**：

```bash
dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- \
  TestCases/bin/Debug/netstandard2.1/TestCases.dll \
  HotfixAOT/Patched/HotfixAOT.patch true NeoStep
```

基线（2026-07-13 实测，同一份 `TestCases.dll`、同一份预生成的 `HotfixAOT.patch`）：
- **Legacy/register 全量**（plain `Debug` + `useRegister=true`）：519 跑，1 失败（518/519）→ 稳定绿基线，回归用。这 1 个未定位。
- **Neo 全量**（`Debug_Neo` + `useRegister=true`，无 filter）：**951 跑 / 0 失败**（全量绿！overhaul wave-2 的 64 个 child 把 189 个失败修到 0）。`Ldftn`/委托（Step 19）/async（Step 20）等早已实现；189 个失败全是真运行时 bug（不是未实现指令），已全部修复。Step 1-26 主干指令均已实现，对应 `NeoStep*` 用例全绿。
- 日常只跑 `NeoStep` 冒烟（**当前 417/0 全绿**，HEAD `f820c644`，2026-07-17；全绿 = 环境正常；`NeoOptHardening` K1 用例在单独过滤下跑）。

注意：`Debug_Neo` 因 `OUTPUT_JIT_RESULT` 宏会打印大量 JIT / 优化器中间结果，属正常。Neo 模式只用 `Debug_Neo` 构建 **CLI**，**不要**用该配置构建 `TestCases`（其产物路径不变）；单测 >10s 多为解释器死循环。完整规则见 `.trae/rules/unittest_guide.md`。

## 代码地图（核心库 `ILRuntime/`）
- `Runtime/Enviorment/AppDomain.cs` — 公共 API 总入口（`LoadAssembly` / `Invoke` / `BeginInvoke` / `Instantiate` / 绑定注册）。
- `Runtime/Intepreter/RegisterVM/JITCompiler.cs` — CIL→`OpCodeR` 翻译入口（`Compile`）。
- `Runtime/Intepreter/ILIntepreter.cs` + `RegisterVM/ILIntepreter.Register.cs`（Legacy `ExecuteR`）+ `RegisterVM/ILIntepreter.Neo.cs`（Neo `ExecuteNeo`）— 解释器主循环（巨型 `switch`）。
- `Runtime/Intepreter/ILTypeInstance.cs` — 对象 / 实例内存布局（Legacy `StackObject[]` / Neo `byte[]+AutoList`）。
- `CLR/TypeSystem/ILType.cs` — 字段布局计算（`TotalPrimitiveSize` / `TotalReferenceCount`）、Neo VTable。
- `Runtime/CLRBinding/` — CLR 绑定 / 重定向代码生成器。
- `Runtime/Stack/StackObject.cs` — Legacy 值表示（12 字节 union）。
- `.trae/documents/object-model-design.md` + `object-model-neo-design.md` — Neo 设计依据。
- `.trae/specs/implement-neo-step*` — 各 Neo step 的实现规格与 checklist。

## 约定 / 坑
- **写大段中文文档**：经 Write 工具会零星把 CJK 字符损坏成 `U+FFFD`（约 0.5%，与 payload 大小相关）；写后用纯 ASCII 码点 PowerShell 校验 / 修复（详见记忆 `cjk-write-encoding-corruption`）。小段写入安全。
- 仓库含大量入库二进制依赖（`Dependencies/*.dll` / `*.pdb`），`git status` 常有此类噪音，属正常。
- 本仓库启用 Git LFS；克隆后依赖 DLL 体积异常时确认 LFS 已拉取。
