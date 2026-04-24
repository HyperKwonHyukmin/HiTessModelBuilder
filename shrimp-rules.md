# Development Guidelines

## Project Overview

- **도구**: `cmb` CLI — AM(Aveva Marine) 3D CSV → FEM 1D Beam → Nastran BDF 변환
- **스택**: .NET 8, C# 12, System.CommandLine, System.Text.Json(Source Generator), xUnit
- **PRD 위치**: `docs/PRD.md` — 모든 요구사항 정의의 단일 출처
- **ROADMAP 위치**: `docs/ROADMAP.md` — Phase별 구현 순서 및 체크리스트

---

## ⛔ 절대 금지 (MUST NOT)

- **`C:\Coding\Csharp\Projects\HiTessModelBuilder_26_01\` 내 파일을 Edit/Write 도구로 수정하지 마라.** Read 도구로 열람만 허용.
- `Cmb.Core` 프로젝트에 외부 NuGet 패키지를 추가하지 마라. `System.Text.Json`(BCL)만 허용.
- Tolerance 값을 코드 내 숫자 리터럴로 하드코딩하지 마라. 반드시 `RunOptions.Tolerances` 레코드 필드를 참조하라.
- `ExtraData: Dictionary<string, string>` 패턴을 사용하지 마라. `EntityCategory` enum 또는 `NodeTags [Flags]` enum으로 대체하라.
- `Console.WriteLine`으로 로깅하지 마라. `ILogger<T>` 인터페이스를 사용하라.
- `Cmb.Core` → `Cmb.Io`, `Cmb.Io` → `Cmb.Pipeline`, `Cmb.Pipeline` → `Cmb.Cli` 방향의 역방향 참조를 추가하지 마라.
- `out/` 디렉토리 파일을 소스 관리(git)에 추가하지 마라.
- Phase 순서를 건너뛰지 마라. A-1 → A-2 → A-3 → A-4 → B-1 → B-2 → C-1 → … 순서를 준수하라.

---

## 프로젝트 아키텍처

### 솔루션 레이아웃

```
ClaudeModelBuilder.sln
Directory.Build.props       ← 전역 설정 파일. 새 프로젝트 추가 시 이 파일에서 상속됨
src/
  Cmb.Core/                 ← 도메인 모델, 기하, JSON 직렬화. 외부 의존 없음
  Cmb.Io/                   ← CSV Reader, BDF Writer, JSON Dumper
  Cmb.Pipeline/             ← Stage, Inspector, Modifier, Spatial 유틸
  Cmb.Cli/                  ← System.CommandLine 진입점
tests/
  Cmb.Core.Tests/
  Cmb.Io.Tests/
  Cmb.Pipeline.Tests/
  Cmb.Integration.Tests/
samples/hitess_mini/        ← fixture CSV (3~5행). 테스트에서 이 경로를 참조
out/raw/ out/model/ out/stages/   ← CLI 산출물. git 추적 안 함
```

### 의존성 방향 (단방향)

```
Cmb.Cli → Cmb.Pipeline → Cmb.Io → Cmb.Core
```

새 기능을 어느 프로젝트에 둘지 결정 시: 도메인 타입이면 `Cmb.Core`, I/O면 `Cmb.Io`, 알고리즘 스테이지면 `Cmb.Pipeline`, CLI 진입점이면 `Cmb.Cli`.

---

## 코드 표준

### 네이밍

| 대상 | 규칙 | 예시 |
|------|------|------|
| 도메인 엔티티 | PascalCase record/class | `BeamElement`, `BeamSection` |
| 기하 struct | `readonly struct`, PascalCase | `Point3`, `Vector3` |
| 인터페이스 | `I` prefix | `IPipelineStage`, `IModelModifier` |
| Enum | PascalCase, `[Flags]`는 복수형 | `EntityCategory`, `NodeTags` |
| Tolerance 필드 | PascalCase + 단위 접미사 `Mm` (mm) | `NodeMergeTol`, `SpatialCellSizeMm` |
| 진단 코드 | SCREAMING_SNAKE | `SHORT_ELEM`, `NODE_MERGED` |

### 타입 규칙

- `Point3`, `Vector3`, `Segment3`는 반드시 `readonly struct` + `IEquatable<T>`. 좌표는 `double`. `int`, `float` 금지.
- 모든 Node/Element ID는 `IdAllocator.Next()`로 발급. 수동 ID 할당 금지.
- `BeamElement.Orientation`은 `Vector3` (로컬 좌표계 z축 방향).
- `NodeTags`는 `[Flags]` enum — 비트 OR로 복수 태그 부여 (`node.Tags |= NodeTags.Weld`).

### JSON 직렬화

- `FeModelJson` Source Generator 컨텍스트를 사용. `JsonSerializer.Serialize(obj)` 직접 호출 금지.
- JSON meta 필드 필수: `phase`, `stageName`, `timestamp`, `unit="mm"`, `schemaVersion="1.0"`.
- `out/raw/*.raw.json`, `out/model/*.initial.json`, `out/stages/{idx:D2}_{name}.json` 경로 패턴 준수.

### BDF 출력

- 모든 BDF 필드는 `BdfField` 8열 고정폭 포맷터를 통해 출력. 직접 `string.Format` 금지.
- 실수값은 지수 표기 clamp 적용 (HiTess `BdfFormatFields.cs` 로직 참조).
- BDF 파일 첫 줄에 SOL/CEND 없이 벌크 데이터만 출력.
- 지원 카드: `GRID`, `CBEAM`, `PBEAML`, `PBARL`, `MAT1`, `RBE2`, `CONM2`, `SPC1`, `ENDDATA`.

---

## 기능 구현 표준

### 새 Pipeline Stage 추가 시

1. `Cmb.Pipeline/Stages/` 아래 `{Name}Stage.cs` 파일 생성.
2. `IPipelineStage` 인터페이스 구현: `string Name`, `int StageIndex`, `Task<StageResult> RunAsync(StageContext ctx)`.
3. `PipelineRunner`의 스테이지 목록에 등록 (순서: `SanityPreprocess → Meshing → NodeEquivalence → Intersection → WeldNode → GroupConnect → UboltRbe → FinalValidation`).
4. `StageResult`에 `List<Diagnostic>` 포함. 진단 코드는 `PRD.md` 해당 스테이지 섹션에 정의된 코드 사용.
5. 스테이지 완료 후 `PipelineRunner`가 자동으로 JSON 덤프 — Stage 내에서 파일 쓰기 금지.

### Inspector vs Modifier 구분

- `IModelInspector`: `FeModel` 읽기 전용 분석 → `IReadOnlyList<Diagnostic>` 반환. **`FeModel` 변경 금지.**
- `IModelModifier`: `FeModel` 받아 변형 후 반환. 원본 객체 대신 수정된 새 상태 반환 권장.

### 수렴 루프 구현 시

```csharp
int iterations = 0;
int changes;
do {
    changes = RunOnePass(model);
    iterations++;
} while (changes > 0 && iterations < ctx.Options.Tolerances.MaxConvergenceIterations);

if (iterations >= ctx.Options.Tolerances.MaxConvergenceIterations)
    ctx.Report.Add(new Diagnostic(DiagnosticSeverity.Warn, "CONVERGENCE_LIMIT_REACHED", ...));
```

### HiTess 참조 코드 사용 시

- 반드시 Read 도구로 해당 파일을 먼저 열람한 후 개념을 포팅.
- `ExtraData` 딕셔너리 패턴이 있으면 강타입 enum으로 대체하여 포팅.
- 로깅 `Console.WriteLine` → `ctx.Logger.LogInformation(...)` 으로 교체.

| 필요한 알고리즘 | 참조 경로 |
|----------------|-----------|
| 공간 해시 | `Pipeline/Utils/SpatialHash.cs`, `ElementSpatialHash.cs` |
| UnionFind | `Pipeline/Utils/UnionFind.cs` |
| 노드 동일성 병합 | `Pipeline/NodeInspector/InspectEquivalenceNodes.cs` |
| 교차 분할 | `Pipeline/ElementModifier/ElementIntersectionSplitModifier.cs` |
| 그룹 연결 | `Pipeline/ElementModifier/ElementGroupTranslationModifier.cs` |
| U-bolt snap | `Pipeline/ElementModifier/UboltSnapToStructureModifier.cs` |
| 단면 매핑 | `Services/Builders/RawFeModelBuilder.cs` |
| BDF 포맷 | `Exporter/BdfFormatFields.cs`, `BdfBuilder.cs` |
| CSV 컬럼 | `Parsers/CsvParser.cs` |

---

## 테스트 표준

- 새 기하 기능: `Cmb.Core.Tests/Geometry/` 아래 단위 테스트 추가.
- BDF Writer 변경: `Verify.Xunit` 스냅샷 골든 테스트 업데이트.
- 새 Stage: `samples/hitess_mini` fixture 기반 입력/출력 수치 테스트 작성.
- 통합 테스트: `Cmb.Integration.Tests/` — 전체 파이프라인 end-to-end.
- 테스트 실행: `dotnet test tests/Cmb.Core.Tests` 또는 `dotnet test ClaudeModelBuilder.sln`.
- **테스트 없이 스테이지를 완료 처리하지 마라.**

---

## 주요 파일 상호작용 규칙

| 수정 대상 | 동시에 확인/수정할 파일 |
|-----------|------------------------|
| `RunOptions.Tolerances`에 새 필드 추가 | `docs/PRD.md` 섹션 FR-C000 Tolerances 표 |
| 새 `BeamSectionKind` 추가 | `FeModelBuilder`의 단면 매핑 switch, BDF Writer의 PBEAML/PBARL 출력 |
| 새 `NodeTags` 비트 추가 | JSON 직렬화 컨텍스트, `docs/json-schema.md` |
| `FeModel` 필드 추가 | `FeModelJson` Source Generator 컨텍스트, JSON meta 스키마 |
| 새 CLI 서브커맨드 추가 | `Cmb.Cli/Program.cs` 루트 커맨드 등록 |
| `samples/hitess_mini` CSV 수정 | 해당 Verify 스냅샷 파일 삭제 후 재생성 |

---

## AI 의사결정 기준

### 어느 프로젝트에 코드를 놓을지

```
도메인 타입/기하/JSON? → Cmb.Core
파일 I/O (CSV/BDF/JSON 파일)? → Cmb.Io
알고리즘 스테이지/공간 자료구조? → Cmb.Pipeline
CLI 커맨드/옵션 파싱? → Cmb.Cli
```

### 기하 라이브러리 추가 여부

- 순수 선형대수(dot, cross, normalize)는 직접 구현. 외부 라이브러리 추가 금지.
- SVD 등 고급 행렬 연산이 필요한 경우에만 `MathNet.Numerics`를 `Cmb.Pipeline`에 추가.

### 스테이지에서 FeModel을 수정해야 할 때

- Inspector(분석 전용)인지 Modifier(변형 허용)인지 먼저 결정.
- 분석 결과를 `Diagnostic`으로 반환하고 실제 변형은 별도 Modifier로 분리.

### Phase 진행 판단

- 현재 Phase의 `dotnet test` 전체 통과 + `out/` 디렉토리에 JSON 덤프 생성 확인 후 다음 Phase 진행.
- ROADMAP.md의 체크리스트 항목을 기준으로 완료 여부 판단.
