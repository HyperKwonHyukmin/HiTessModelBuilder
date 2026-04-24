# ClaudeModelBuilder PRD

## 1. 프로젝트 개요

### 1.1 목적

AM(Aveva Marine) 3D 설계에서 추출한 Beam류(H, L, Rod, Tube 등) CSV 데이터를 FEM 1D 유한요소 Beam 모델로 변환하고, Nastran BDF 파일을 생성하는 .NET 8 콘솔 CLI 도구를 신규 작성한다.

레거시 프로젝트(`HiTessModelBuilder_26_01`)의 알고리즘 개념을 선별 참조하되, 신규 4-프로젝트 구조로 재설계한다. 레거시 프로젝트는 읽기 전용 참조만 허용하며 코드 수정은 금지한다.

### 1.2 범위

**포함**

- AM CSV 파일 파싱 (Structure/Pipe/Equipment 3종)
- FEM 노드·빔 요소·강체 요소·집중질량 모델 구축
- 알고리즘 8단계 파이프라인(위상 정규화 → 교차 분할 → 용접 태깅 → RBE2 생성)
- Nastran BDF 출력 (GRID/CBEAM/PBEAML/PBARL/MAT1/RBE2/CONM2/SPC1)
- 각 단계별 FeModel JSON 덤프
- xUnit 기반 단위·통합·골든 테스트

**제외**

- GUI 또는 웹 인터페이스
- Nastran 솔버 실행 및 결과 후처리
- AM 이외의 3D CAD 포맷 지원
- 비선형 해석 요소 (CBUSH, CGAP 등)
- 레거시 `HiTessModelBuilder_26_01` 코드 수정

### 1.3 이해관계자

| 역할 | 내용 |
|------|------|
| 개발자 | 본 PRD를 기반으로 구현에 착수하는 엔지니어 |
| FEM 검증자 | out/stages JSON을 시각화 도구로 검증하는 구조 해석 엔지니어 |
| 레거시 참조 소스 | `HiTessModelBuilder_26_01` (읽기 전용, 알고리즘 개념 참조) |

---

## 2. 솔루션 구조 및 프로젝트 레이아웃

```
ClaudeModelBuilder.sln
Directory.Build.props          (Nullable=enable, TreatWarningsAsErrors=true)
src/
  Cmb.Core/                    classlib  도메인 모델, 기하 primitive, JSON 직렬화
  Cmb.Io/                      classlib  CSV Reader, BDF Writer, JSON Dumper
  Cmb.Pipeline/                classlib  Stage, Inspector, Modifier, Spatial 유틸
  Cmb.Cli/                     exe       System.CommandLine, Program.cs
tests/
  Cmb.Core.Tests/
  Cmb.Io.Tests/
  Cmb.Pipeline.Tests/
  Cmb.Integration.Tests/
samples/hitess_mini/           3~5행 fixture CSV
out/raw/
out/model/
out/stages/                    CLI 산출물 (gitignore)
docs/
  architecture.md
  json-schema.md
```

### 2.1 프로젝트 간 의존성 방향

```
Cmb.Cli ──→ Cmb.Pipeline ──→ Cmb.Core   (파이프라인, IO 독립)
Cmb.Cli ──→ Cmb.Io       ──→ Cmb.Core   (IO 어댑터)
```

**핵심 제약**: `Cmb.Pipeline`은 `Cmb.Core`만 참조한다. `Cmb.Io`(CSV/BDF)에 대한 직접 의존은 금지한다. 이를 통해 동일한 파이프라인을 CSV, IFC, XML 등 어떤 입력 소스에서도 재사용할 수 있다.

순환 참조 금지. `Cmb.Core`는 외부 NuGet에 의존하지 않는다(`System.Text.Json` BCL 제외).

---

## 3. 기능 요구사항

### Phase A — CSV 파싱

#### FR-A001 솔루션 스켈레톤 생성

- **설명**: `.sln`, 4개 `src` 프로젝트, 4개 `tests` 프로젝트, `Directory.Build.props` 생성
- **수용 기준**:
  - `dotnet build` 경고 0, 오류 0
  - `Nullable=enable`, `TreatWarningsAsErrors=true` 전역 적용
  - 각 테스트 프로젝트가 대응 src 프로젝트를 프로젝트 참조함

#### FR-A002 기하 Primitive 구현

- **설명**: `Cmb.Core.Geometry` 네임스페이스에 `Point3`, `Vector3`, `Segment3` 구현
- **수용 기준**:
  - 모두 `readonly struct`, `double` 좌표, `IEquatable<T>` 구현
  - `Point3`: X, Y, Z 프로퍼티, `DistanceTo(Point3)`, `+/-` 연산자 (Vector3와 교차)
  - `Vector3`: X, Y, Z, `Length`, `Normalize()`, Dot/Cross 곱, `+/-/*` 연산자
  - `Segment3`: Start/End (`Point3`), `Length`, `Midpoint`, `ClosestPointTo(Point3)`
  - 단위: mm (소수점 표현, 정수 변환 금지)
  - 단위 테스트 20개 이상 통과

#### FR-A003 도메인 모델 뼈대 구현

- **설명**: `Cmb.Core.Model` 네임스페이스 핵심 엔티티 구현
- **수용 기준**:
  - `Node`: `int Id`, `Point3 Position`, `NodeTags Tags` (변경 불가 초기화, setter 없음)
  - `BeamElement`: `int Id`, `int StartNodeId`, `int EndNodeId`, `int PropertyId`, `EntityCategory Category`, `Vector3 Orientation`
  - `RigidElement`: `int Id`, `int IndependentNodeId`, `IReadOnlyList<int> DependentNodeIds`, `string Remark`
  - `PointMass`: `int Id`, `int NodeId`, `double Mass`
  - `NodeTags`: `[Flags] enum` — `None=0`, `Weld=1`, `Intersection=2`, `Merged=4`, `Boundary=8`
  - `BeamSectionKind`: `enum { H, L, Rod, Tube, Box, Channel, Bar }`
  - `BeamSection`: `record` — `int Id`, `BeamSectionKind Kind`, `double[] Dims`, `int MaterialId`
  - `EntityCategory`: `enum { Structure, Pipe, Equipment }`
  - `IdAllocator`: `int Next()`, 스레드 안전 불필요 (단일 스레드 파이프라인)

#### FR-A004 FeModel 및 JSON 직렬화 구현

- **설명**: `Cmb.Core.Model.Context.FeModel` 및 `Cmb.Core.Serialization.FeModelJson` 구현
- **수용 기준**:
  - `FeModel`: `List<Node>`, `List<BeamElement>`, `List<RigidElement>`, `List<PointMass>`, `List<BeamSection>`, `List<Material>`, `LengthUnit` (상수 `"mm"`)
  - `FeModelJson`: `System.Text.Json` Source Generator 사용, AOT 호환
  - `FeModel.ToJson()` → UTF-8 문자열 반환
  - `FeModel.FromJson(string)` → `FeModel` 반환
  - JSON 스키마 섹션 6에 정의된 필드명과 1:1 매핑
  - 왕복 테스트 (직렬화 → 역직렬화 → 동치) 통과

#### FR-A005 HiTess CSV 리더 구현

- **설명**: `Cmb.Io.Csv` 네임스페이스에 3종 리더 구현
- **수용 기준**:
  - `HiTessStructureCsvReader`: Structure 행 파싱 (컬럼: NodeA, NodeB, ProfilType, Dims, Category 등)
  - `HiTessPipeCsvReader`: Pipe 행 파싱 (컬럼: NodeA, NodeB, OD, WT, Weld 힌트 등)
  - `HiTessEquipCsvReader`: Equipment 행 파싱 (컬럼: NodeId, Mass, Cog 등)
  - `CsvDesignLoader`: 3종 리더를 조합하여 `RawDesignData` 반환
  - `RawDesignData`: `IReadOnlyList<RawBeamRow>`, `IReadOnlyList<RawPipeRow>`, `IReadOnlyList<RawEquipRow>`
  - 헤더 행 자동 스킵, 빈 행 무시
  - `samples/hitess_mini` fixture 기반 Verify.Xunit 스냅샷 테스트 통과

#### FR-A006 `cmb parse` CLI 서브커맨드 구현

- **설명**: Phase A 엔드포인트
- **수용 기준**:
  - `cmb parse --input <folder> --output <dir>` 실행 가능
  - `--input` 폴더 내 CSV 파일 자동 탐색 (Structure/Pipe/Equipment 구분)
  - `out/raw/*.raw.json` 생성
  - JSON meta 포함: `phase="A"`, `stageName="parse"`, `timestamp`, `unit="mm"`, `schemaVersion="1.0"`
  - 입력 폴더 없을 경우 오류 메시지 + exit code 1 반환
  - `--help` 출력 정상 동작

**체크포인트 A**: `out/raw` JSON을 외부 시각화 도구로 검증 후 Phase B 진행

---

### Phase B — 원본 모델 출력

#### FR-B001 FeModelBuilder 구현

- **설명**: `RawDesignData` → `FeModel` 변환 (알고리즘 미적용)
- **수용 기준**:
  - 각 `RawBeamRow` → `BeamElement` + 양 끝점 `Node` 생성
  - `BeamSection` 중복 제거(Dedupe): 동일 `Kind + Dims` 조합은 단일 `PropertyId` 공유
  - `Material` 기본값 자동 생성: `Steel E=206000 MPa, ν=0.3, ρ=7.85e-9 ton/mm³`
  - 모든 `Node.Id`, `BeamElement.Id`, `BeamSection.Id`는 `IdAllocator`에서 발급
  - 장비(`RawEquipRow`) → `PointMass` 변환
  - 파이프(`RawPipeRow`) → `BeamElement(Category=Pipe)` 변환, `BeamSectionKind.Tube` 사용

#### FR-B002 Nastran BDF Writer 구현

- **설명**: `Cmb.Io.Nastran` 네임스페이스 BDF 출력
- **수용 기준**:
  - `BdfField`: 8열 고정폭 포맷터, 실수 지수 표기 clamp (HiTess `BdfFormatFields` 로직 참조)
  - `BdfWriter`: 아래 카드 출력
    - `GRID` (id, cp, x, y, z, cd)
    - `CBEAM` (eid, pid, ga, gb, x1, x2, x3)
    - `PBEAML` (pid, mid, group, type, dims)
    - `PBARL` (pid, mid, group, type, dims)
    - `MAT1` (mid, E, G, nu, rho)
    - `RBE2` (eid, gn, cm, gi...)
    - `CONM2` (eid, g, cid, m)
    - `SPC1` (sid, c, g...)
    - `ENDDATA`
  - 출력 파일이 Nastran free-field 파서로 재파싱 가능 (왕복 검증)

#### FR-B003 `cmb build-raw` CLI 서브커맨드 구현

- **설명**: Phase B 엔드포인트
- **수용 기준**:
  - `cmb build-raw --input <folder> --output <dir>` 실행 가능
  - `out/model/*.initial.json` + `*.bdf` 생성
  - JSON meta: `phase="B"`, `stageName="initial"`, `unit="mm"`, `schemaVersion="1.0"`
  - BDF 파일 첫 줄에 SOL/CEND 없이 벌크 데이터만 포함 (검증 도구 호환)

#### FR-B004 Element 추적성(Traceability) 구현

- **설명**: CSV 행의 Name ID → FE Element ID 매핑을 보존하고, 이후 파이프라인 스테이지에서 요소가 어떻게 변화했는지(쪼개기·노드 병합·이동·제거)를 JSON에 기록한다. 디버깅 및 검증용.
- **수용 기준**:
  - `BeamElement`, `PointMass`, `RigidElement`에 `string? SourceName` 프로퍼티 추가 (optional, 기본값 null)
  - `BeamElement`에 `int? ParentElementId` 프로퍼티 추가 (쪼개기 스테이지에서 자식 생성 시 사용)
  - `TraceEvent` 불변 레코드 신규 추가: `TraceAction`, `StageName`, `ElementId?`, `NodeId?`, `RelatedElementId?`, `RelatedNodeId?`, `Note?`
  - `TraceAction` 열거: `ElementCreated | ElementSplit | NodeMerged | NodeMoved | ElementRemoved`
  - `FeModel.TraceLog`: `List<TraceEvent>` — 모든 변환 이벤트 누적
  - `FeModelBuilder`가 요소 생성 시 `sourceName = row.Name` 설정 및 `TraceAction.ElementCreated` 이벤트 기록
  - JSON 스키마 v1.1로 업데이트: `elements[].sourceName?`, `elements[].parentElementId?`, `pointMasses[].sourceName?`, `rigids[].sourceName?`, `trace?` 배열 추가
  - `trace` 배열: 비어있으면 JSON 키 자체 생략 (`WhenWritingNull`/`WhenWritingDefault`)

#### FR-B005 파이프라인 재사용성(Pipeline Reusability) 확보

- **설명**: `Cmb.Pipeline`이 `Cmb.Io`(CSV/BDF)와 완전히 분리되어, 다른 프로젝트에서 `FeModel`을 직접 생성하고 동일한 파이프라인을 재사용할 수 있게 한다.
- **수용 기준**:
  - `Cmb.Pipeline.csproj`에서 `<ProjectReference Cmb.Io>` 제거 → `<ProjectReference Cmb.Core>` 단독 참조
  - `Microsoft.Extensions.Logging.Abstractions 8.0.0` NuGet 추가 (`Cmb.Pipeline`에서 `ILogger` 사용)
  - `PipelineRunner.Run(FeModel, stages, options, logger, onStageComplete?)` 정적 메서드 시그니처 확정
  - `onStageComplete(stageName, FeModel)` 콜백: CLI가 이 콜백에서 JSON 덤프 수행 (Pipeline은 JSON 직렬화 모름)
  - `Cmb.Pipeline`이 `Cmb.Io` 네임스페이스의 어떤 타입도 참조하지 않음을 빌드 의존성으로 보장

**체크포인트 B**: `out/model` JSON + BDF를 시각화 및 Nastran 문법 검증 후 Phase C 진행

---

### Phase C — 알고리즘 파이프라인

#### FR-C000 파이프라인 인프라 구현

- **설명**: `Cmb.Pipeline` 핵심 추상화 구현
- **수용 기준**:
  - `IPipelineStage`: `string Name`, `StageIndex`, `Task<StageResult> RunAsync(StageContext)`
  - `IModelInspector`: `FeModel`을 받아 진단 목록 반환 (mutation 금지, 컴파일 타임 강제)
  - `IModelModifier`: `FeModel`을 받아 변형 후 반환
  - `PipelineRunner`: 스테이지 순차 실행, `--stopat` 지원, 각 스테이지 후 JSON 덤프
  - `StageContext`: `FeModel`, `RunOptions`, `PipelineReport`, `ILogger`
  - `RunOptions`: 아래 `Tolerances` 레코드 포함
  - `PipelineReport`: `List<Diagnostic>` 누적
  - `Diagnostic`: `DiagnosticSeverity Severity`, `string Code`, `string Message`, `int? ElemId`, `int? NodeId`
  - `DiagnosticSeverity`: `Info`, `Warn`, `Error`
  - `SpatialHash<T>`: 셀 크기 파라미터화, `Insert`, `QueryNeighbors`
  - `ElementSpatialHash`: `BeamElement` 전용 공간 해시
  - `UnionFind`: `Union(a,b)`, `Find(a)`, `GetGroups()`
  - `ProjectionUtils`: `ClosestPointOnSegment`, `SegmentToSegmentDistance`

**Tolerances 레코드 필드**

| 필드명 | 타입 | 기본값 | 설명 |
|--------|------|--------|------|
| `NodeMergeTol` | `double` | `1.0` mm | 노드 동일 좌표 허용 오차 |
| `IntersectionTol` | `double` | `0.1` mm | 교차점 판정 오차 |
| `UboltSnapMaxDist` | `double` | `50.0` mm | U-bolt snap 최대 거리 |
| `ShortElemMin` | `double` | `5.0` mm | 유효 최소 요소 길이 |
| `SpatialCellSizeMm` | `double` | `200.0` mm | 공간 해시 셀 크기 |
| `MaxConvergenceIterations` | `int` | `50` | 수렴 루프 최대 반복 횟수 |
| `MeshingMaxLengthStructure` | `double` | `2000.0` mm | Structure beam 최대 분할 길이 |
| `MeshingMaxLengthPipe` | `double` | `1000.0` mm | Pipe beam 최대 분할 길이 |

#### FR-C001 SanityPreprocessStage

- **설명**: 입력 데이터 정합성 검사 및 전처리
- **전제조건**: `FeModel`이 `FeModelBuilder`에서 생성된 초기 상태
- **수행 작업**:
  1. 중복 요소 제거 (동일 `StartNodeId`/`EndNodeId` 쌍)
  2. 단축 요소 제거 (`Length < Tolerances.ShortElemMin`)
  3. 공선 노드 병합 (세 노드 A-B-C에서 B가 AC 선분 위에 있으면 B 제거, A-C 직결)
  4. 노드 참조 정합성 검사 (존재하지 않는 `NodeId` 참조 시 `Error` 진단)
- **후조건**: 중복 없음, 단축 요소 없음, 모든 요소-노드 참조 유효
- **출력 진단 코드**: `DUPLICATE_ELEM`, `SHORT_ELEM`, `COLLINEAR_NODE`, `ORPHAN_NODE_REF`

#### FR-C002 MeshingStage

- **설명**: 긴 Beam 요소를 등분할
- **전제조건**: `SanityPreprocessStage` 완료
- **수행 작업**:
  - `Category=Structure`: `Length > MeshingMaxLengthStructure` 이면 `ceil(Length / MeshingMaxLengthStructure)` 등분할
  - `Category=Pipe`: `Length > MeshingMaxLengthPipe` 이면 동일 방식 등분할
  - 분할 시 중간 노드 생성 (`IdAllocator.Next()`), 원래 요소 제거 후 분할 요소 삽입
  - `Equipment` 요소는 분할하지 않음
- **후조건**: 모든 `Structure` 요소 `Length <= MeshingMaxLengthStructure`, 모든 `Pipe` 요소 `Length <= MeshingMaxLengthPipe`
- **출력 진단 코드**: `MESHING_SPLIT`

#### FR-C003 NodeEquivalenceStage

- **설명**: 근접 노드 병합 (O(N log N) sweep-and-prune)
- **전제조건**: `MeshingStage` 완료
- **알고리즘**:
  1. 노드를 X 좌표로 정렬
  2. 슬라이딩 윈도우: 현재 노드와 윈도우 내 노드 간 3D 거리 `< NodeMergeTol` 이면 동일 노드로 판정
  3. `UnionFind`로 병합 그룹 결정
  4. 각 그룹에서 최소 ID 노드를 대표 노드로 선정
  5. 모든 `BeamElement`, `RigidElement`의 노드 ID를 대표 노드 ID로 교체
  6. 루프 요소(StartNodeId == EndNodeId 된 것) 제거
- **후조건**: 거리 `< NodeMergeTol`인 노드 쌍 없음
- **출력 진단 코드**: `NODE_MERGED`

#### FR-C004 IntersectionStage

- **설명**: 빔 요소 교차점에서 요소 분할 및 노드 공유 (수렴 루프)
- **전제조건**: `NodeEquivalenceStage` 완료
- **알고리즘 (Dan Sunday segment-segment)**:
  1. `ElementSpatialHash`로 후보 쌍 추출
  2. 두 선분의 최근접 점 계산, 거리 `< IntersectionTol` 이면 교차 판정
  3. 교차점에 새 노드 삽입, 두 요소를 각각 2개로 분할
  4. 분할 후 전체 재검사(수렴 루프), `MaxConvergenceIterations` 초과 시 `Warn` 진단 후 종료
- **후조건**: 교차 쌍이 없거나 `MaxConvergenceIterations` 도달
- **출력 진단 코드**: `INTERSECTION_SPLIT`, `CONVERGENCE_LIMIT_REACHED`

#### FR-C005 WeldNodeStage

- **설명**: CSV weld 힌트 기반 `NodeTags.Weld` 부여
- **전제조건**: `IntersectionStage` 완료
- **수행 작업**:
  - `RawPipeRow.IsWeld == true`인 행의 끝점 좌표와 가장 가까운 노드(거리 `< NodeMergeTol`)를 찾아 `NodeTags.Weld` 추가
  - 힌트 없으면 아무것도 하지 않음 (경고 없음)
- **후조건**: 용접 힌트가 있는 노드에 `NodeTags.Weld` 적용됨
- **출력 진단 코드**: `WELD_TAG_APPLIED`, `WELD_HINT_UNMATCHED`

#### FR-C006 GroupConnectStage

- **설명**: 연결 그룹 감지 및 최대 그룹으로 근접 단독 그룹 연결
- **전제조건**: `WeldNodeStage` 완료
- **수행 작업**:
  1. `UnionFind`로 `BeamElement` 연결 그룹 식별
  2. 최대 그룹 선정
  3. 비최대 그룹의 각 요소에 대해 최대 그룹의 가장 가까운 요소 검색
  4. 거리 기반 단순 판정: 최근접 거리 `< UboltSnapMaxDist * 2` 이면 중간 연결 노드 생성 후 단축 빔으로 연결 (`NodeTags.Boundary` 부여)
  5. 연결 불가한 고립 그룹은 `Warn` 진단 발행
- **후조건**: 단일 연결 그룹(또는 고립 그룹 Warn 발행)
- **출력 진단 코드**: `GROUP_CONNECTED`, `ISOLATED_GROUP`

#### FR-C007 UboltRbeStage

- **설명**: Pipe U-bolt를 Structure에 snap하여 `RBE2` 또는 `ForcedSpc` 생성
- **전제조건**: `GroupConnectStage` 완료
- **알고리즘 (2-phase snap)**:
  - Phase 1 (힌트 기반): `RawPipeRow`에 U-bolt 힌트 좌표가 있으면 해당 좌표 근처 `Structure` 요소에 snap
  - Phase 2 (근접도 휴리스틱 fallback): 힌트 없으면 모든 `Pipe` 요소 끝점에서 가장 가까운 `Structure` 노드 탐색, 거리 `< UboltSnapMaxDist` 이면 snap
  - Snap 성공: `RigidElement(Remark="UBOLT")` 생성, 독립 노드 = Structure 노드, 종속 노드 = Pipe 노드
  - Snap 실패: `NodeTags.Boundary` 부여 + `Warn` 진단
- **후조건**: U-bolt 힌트가 있는 Pipe 끝점에 `RigidElement` 또는 `Boundary` 태그 적용
- **출력 진단 코드**: `UBOLT_RBE2_CREATED`, `UBOLT_SNAP_FAILED`

#### FR-C008 FinalValidationStage + ExportStage

- **설명**: 최종 검증 및 BDF 출력
- **수행 작업 (FinalValidationStage)**:
  1. 자유단 노드 검사: 1개 요소에만 연결된 노드 중 `NodeTags.Boundary` 없는 것 → `Warn`
  2. 고아 노드 검사: 어떤 요소에도 참조되지 않는 노드 → `Warn`
  3. 누락 Property 검사: `BeamElement.PropertyId`에 해당하는 `BeamSection` 없음 → `Error`
  4. 누락 Material 검사: `BeamSection.MaterialId`에 해당하는 `Material` 없음 → `Error`
  5. `Error` 진단이 1개 이상이면 BDF 출력 차단, exit code 2 반환
- **수행 작업 (ExportStage)**:
  - 최종 BDF 파일 생성 (`BdfWriter` 사용)
  - 최종 JSON 덤프 (`out/stages/08_FinalValidation.json`)
- **출력 진단 코드**: `FREE_END_NODE`, `ORPHAN_NODE`, `MISSING_PROPERTY`, `MISSING_MATERIAL`

#### FR-C009 `cmb build-full` CLI 서브커맨드 구현

- **설명**: Phase C 전체 파이프라인 엔드포인트
- **수용 기준**:
  - `cmb build-full --input <folder> --output <dir> [--stopat <stageName|index>] [--dump-json] [--dump-bdf-per-stage]` 실행 가능
  - `--stopat`: 지정 스테이지 완료 후 중단 (이름 또는 0-based 인덱스)
  - `--dump-json`: 각 스테이지 후 `out/stages/{idx:D2}_{stageName}.json` 생성 (기본 off)
  - `--dump-bdf-per-stage`: 각 스테이지 후 BDF 스냅샷 생성 (기본 off)
  - `Error` 진단 존재 시 exit code 2, 경고만 있으면 exit code 0
  - 전체 파이프라인 `PipelineReport` 요약을 stdout으로 출력

---

## 4. 비기능 요구사항

### NFR-001 빌드 품질

- `dotnet build` 경고 0, 오류 0
- `Nullable=enable` 전역 적용, 모든 nullable 경고를 오류로 처리
- `TreatWarningsAsErrors=true` 전역 적용

### NFR-002 테스트 커버리지

- `Cmb.Core` 라인 커버리지 80% 이상
- `Cmb.Pipeline` 각 Stage당 최소 2개 단위 테스트
- 전체 커버리지 목표 70% 이상 (골든 테스트 포함)

### NFR-003 성능

- `hitess_mini` fixture (5행) 전체 파이프라인 완료 시간: 1초 이내
- `NodeEquivalenceStage` 알고리즘 복잡도: O(N log N) (N = 노드 수)
- `IntersectionStage` 공간 해시 사용 필수 (O(N²) 전수 탐색 금지)

### NFR-004 호환성

- .NET 8.0 이상
- C# 12 이상 언어 기능 사용 가능 (primary constructor, collection expression 등)
- Nastran BDF: MSC Nastran 2019 이상 벌크 데이터 섹션 호환

### NFR-005 로깅

- `Console.WriteLine` 직접 호출 금지 (레거시 패턴 배제)
- `Microsoft.Extensions.Logging.ILogger<T>` 사용
- 파이프라인 실행 시 각 Stage 시작/완료 로그 출력 (Information 수준)
- Diagnostic 발행 시 대응 로그 수준 (Info/Warn/Error) 자동 매핑

### NFR-006 JSON 스키마 버전 관리

- `meta.schemaVersion` 필드 필수
- 스키마 변경 시 `docs/json-schema.md` 업데이트 및 버전 번호 증가
- 하위 호환 변경: 마이너 버전 증가, 파괴적 변경: 메이저 버전 증가

---

## 5. 도메인 모델 명세

### 5.1 Cmb.Core.Geometry

#### Point3

```csharp
readonly struct Point3 : IEquatable<Point3>
{
    double X, Y, Z
    static Point3 Origin                         // (0,0,0)
    double DistanceTo(Point3 other)
    Point3 operator+(Point3, Vector3)
    Point3 operator-(Point3, Vector3)
    Vector3 operator-(Point3 a, Point3 b)        // 방향 벡터 반환
}
```

- **불변식**: 좌표는 유한한 double (NaN/Infinity 금지 — 생성자에서 검증)

#### Vector3

```csharp
readonly struct Vector3 : IEquatable<Vector3>
{
    double X, Y, Z
    double Length
    Vector3 Normalize()                          // Length==0 이면 Zero 반환
    static Vector3 Zero
    double Dot(Vector3 other)
    Vector3 Cross(Vector3 other)
    Vector3 operator+(Vector3, Vector3)
    Vector3 operator-(Vector3, Vector3)
    Vector3 operator*(Vector3, double)
    Vector3 operator*(double, Vector3)
}
```

#### Segment3

```csharp
readonly struct Segment3
{
    Point3 Start, End
    double Length
    Point3 Midpoint
    Point3 ClosestPointTo(Point3 p)              // 매개변수 t ∈ [0,1] clamping
    double DistanceTo(Point3 p)
}
```

---

### 5.2 Cmb.Core.Model

#### Node

```csharp
sealed class Node
{
    int Id                         // 양의 정수, IdAllocator 발급
    Point3 Position                // mm 단위
    NodeTags Tags                  // Flags 복합 가능
}
```

#### BeamElement

```csharp
sealed class BeamElement
{
    int Id
    int StartNodeId
    int EndNodeId                  // StartNodeId != EndNodeId (FinalValidation에서 검증)
    int PropertyId
    EntityCategory Category
    Vector3 Orientation            // beam 단면 방향 벡터
}
```

#### RigidElement

```csharp
sealed class RigidElement
{
    int Id
    int IndependentNodeId
    IReadOnlyList<int> DependentNodeIds    // 1개 이상
    string Remark                          // "UBOLT" 등
}
```

#### BeamSection (record)

```csharp
record BeamSection(
    int Id,
    BeamSectionKind Kind,
    double[] Dims,
    int MaterialId
)
```

**BeamSectionKind별 Dims 레이아웃**

| Kind | Dims 순서 | 예시 (mm) |
|------|-----------|-----------|
| H | [H, B, tw, tf] | [400, 200, 10, 16] |
| L | [H, B, tw, tf] | [100, 100, 8, 8] |
| Rod | [R] | [50] |
| Tube | [OD, WT] | [165.1, 7.1] |
| Box | [H, B, tw, tf] | [200, 200, 12, 12] |
| Channel | [H, B, tw, tf] | [200, 80, 8, 12] |
| Bar | [H, B] | [100, 50] |

#### Material (record)

```csharp
record Material(int Id, string Name, double E, double Nu, double Rho)
// 기본값: new Material(1, "Steel", 206000.0, 0.3, 7.85e-9)
```

---

## 6. JSON 출력 스키마 명세

### 6.1 스키마 버전: 1.1 (현재)

v1.0 대비 변경: `elements[].sourceName?`, `elements[].parentElementId?`, `pointMasses[].sourceName?`, `rigids[].sourceName?`, `trace?` 배열 추가. 하위 호환 (기존 v1.0 필드 무변경).

```json
{
  "meta": {
    "phase": "B",
    "stageName": "initial",
    "timestamp": "2026-04-24T10:00:00Z",
    "unit": "mm",
    "schemaVersion": "1.1"
  },
  "nodes":    [{ "id": 1, "x": 0.0, "y": 0.0, "z": 0.0, "tags": ["Weld"] }],
  "elements": [{ "id": 1, "type": "BEAM", "startNode": 1, "endNode": 2, "propertyId": 10,
                 "category": "Structure", "orientation": [0,0,1],
                 "sourceName": "=268454733/2974123_0" }],
  "rigids":   [{ "id": 1, "independentNode": 5, "dependentNodes": [6,7,8], "remark": "UBOLT" }],
  "properties": [{ "id": 10, "kind": "H", "dims": [368,32,200,10], "materialId": 1 }],
  "materials": [{ "id": 1, "name": "Steel", "E": 206000, "nu": 0.3, "rho": 7.85e-9 }],
  "pointMasses": [],
  "diagnostics": [],
  "trace": [
    { "action": "ElementCreated", "stage": "initial", "elemId": 1, "note": "=268454733/2974123_0" }
  ]
}
```

Phase C 쪼개기 이후 trace 예시:
```json
{ "action": "ElementSplit",  "stage": "IntersectionStage",    "elemId": 5, "relatedElemId": 1, "note": "split at node 47" },
{ "action": "NodeMerged",    "stage": "NodeEquivalenceStage", "nodeId": 12, "relatedNodeId": 8, "note": "merged within 1.0mm" }
```

### 6.2 출력 위치 규칙

| Phase | 위치 | 파일명 패턴 |
|-------|------|-------------|
| A | `out/raw/` | `*.raw.json` |
| B | `out/model/` | `*.initial.json`, `*.bdf` |
| C | `out/stages/` | `{idx:D2}_{stageName}.json` |

---

## 7. CLI 인터페이스 명세

### 7.1 공통 규칙

- 루트 커맨드: `cmb`
- 오류 시 stderr 출력 + 비0 exit code
- `--version`, `--help` 지원

### 7.2 서브커맨드 요약

| 커맨드 | 입력 | 출력 | exit code |
|--------|------|------|-----------|
| `cmb parse --input <dir> --output <dir>` | CSV 폴더 | `out/raw/*.raw.json` | 0 / 1 / 2 |
| `cmb build-raw --input <dir> --output <dir>` | CSV 폴더 | `out/model/*.initial.json` + `*.bdf` | 0 / 1 |
| `cmb build-full --input <dir> --output <dir> [--stopat] [--dump-json] [--dump-bdf-per-stage]` | CSV 폴더 | `out/stages/*.json` + `final.bdf` | 0 / 1 / 2 |

---

## 8. 알고리즘 명세

### 8.1 NodeEquivalenceStage (sweep-and-prune)

```
1. nodes를 X 기준 오름차순 정렬
2. UnionFind 초기화
3. left = 0
4. for right in 0..N-1:
     while nodes[right].X - nodes[left].X > NodeMergeTol: left++
     for i in left..(right-1):
       if Distance3D(nodes[i], nodes[right]) < NodeMergeTol:
         UnionFind.Union(nodes[i].Id, nodes[right].Id)
5. 각 그룹에서 Min(Id)를 canonical 선정
```

### 8.2 IntersectionStage (Dan Sunday)

```
수렴 루프:
  iteration = 0
  do:
    pairs = SpatialHash 후보 쌍
    changed = false
    for each (e1, e2) in pairs:
      (dist, p1, p2) = SegmentToSegmentDistance(e1, e2)
      if dist < IntersectionTol:
        pt = (p1 + p2) / 2
        SplitElement(e1, pt); SplitElement(e2, pt)
        changed = true
    iteration++
  while changed AND iteration < MaxConvergenceIterations
```

### 8.3 BdfField 8열 포맷터

- 정수: 우측 정렬, 8열
- 실수: 소수점 표기 우선, 8열 초과 시 지수 표기 (대문자 E)
- NaN/Infinity: `ArgumentException`
- 빈 필드: 8개 공백

---

## 9. 테스트 요구사항

| 테스트 유형 | 대상 | 최소 수 | 도구 |
|-------------|------|---------|------|
| 단위 | `Point3`, `Vector3`, `Segment3` | 20 | xUnit + FluentAssertions |
| 단위 | `BdfField` 포맷터 | 10 | xUnit |
| 단위 | `UnionFind`, `SpatialHash` | 10 | xUnit |
| 단위 | 각 `IPipelineStage` | 2 이상 | xUnit |
| 스냅샷 | CSV 리더 3종 | fixture당 1 | Verify.Xunit |
| 스냅샷 | `BdfWriter` 출력 | 1 | Verify.Xunit |
| 통합 | `cmb parse` / `cmb build-raw` / `cmb build-full` end-to-end | 5 | xUnit |

**커버리지 목표**: Cmb.Core 80%, Cmb.Io 70%, Cmb.Pipeline 70%, 전체 70%

---

## 10. 비채택 결정 목록

| 패턴 | 출처 | 배제 이유 | 대체 설계 |
|------|------|-----------|-----------|
| `ExtraData: Dictionary<string, string>` | HiTess 전반 | 문자열 키 오타 위험, 타입 안전성 없음 | `EntityCategory` enum + `NodeTags [Flags]` |
| `Console.WriteLine` 로깅 | HiTess Pipeline | 테스트 불가, 형식 미제어 | `ILogger<T>` |
| 단일 프로젝트 구조 | HiTess 전체 | 관심사 미분리, 테스트 불가 | 4-프로젝트 계층 구조 |
| 하드코딩 tolerance | HiTess 파이프라인 곳곳 | 재현 불가, 튜닝 불가 | `RunOptions.Tolerances` 레코드 |
| 0 테스트 | HiTess 전체 | 회귀 감지 불가 | xUnit + Verify.Xunit 의무화 |
| Inspector/Modifier 미분리 | HiTess 파이프라인 | Mutation 의도 불명확 | 인터페이스 분리 (`IModelInspector` / `IModelModifier`) |
| 무한 수렴 루프 | HiTess Pipeline | 비정상 데이터 시 무한 반복 | `MaxConvergenceIterations=50` 상한 명시 |
| 좌표계/단위 암묵 가정 | HiTess 전체 | 단위 혼용 버그 위험 | `LengthUnit="mm"` 상수, `meta.unit` JSON 필드 |

---

## 11. 기술 제약사항

| 항목 | 값 |
|------|----|
| .NET 버전 | .NET 8.0 |
| C# 버전 | C# 12 |
| 좌표 단위 | mm |
| 탄성계수 단위 | MPa (N/mm²) |
| 밀도 단위 | ton/mm³ |
| 기본 Material | Steel: E=206000, ν=0.3, ρ=7.85e-9 |
| 레거시 참조 | 읽기 전용. 프로젝트 참조 금지, 코드 포팅 후 신규 작성 |

---

## 12. 용어 사전

| 용어 | 정의 |
|------|------|
| AM | Aveva Marine. 선박·해양 구조물 3D CAD 소프트웨어 |
| FEM | Finite Element Method. 유한요소법 |
| BDF | Bulk Data File. Nastran 입력 파일 형식 |
| GRID | Nastran 노드 카드 |
| CBEAM | Nastran beam 요소 연결 카드 |
| PBEAML / PBARL | Nastran 표준 형강 단면 Property 카드 |
| MAT1 | Nastran 등방성 선형 재료 카드 |
| RBE2 | Rigid Body Element 2. 독립 노드 1개 + 종속 노드 N개 강체 구속 |
| CONM2 | 집중 질량 요소 |
| SPC1 | 단일점 경계 조건 |
| Node Equivalence | 거리 허용오차 내 근접 노드를 하나로 병합 |
| Sweep-and-Prune | 정렬 기반 슬라이딩 윈도우로 O(N log N) 후보 쌍 추출 알고리즘 |
| Dan Sunday | 3D 선분-선분 최근접점 알고리즘 |
| U-bolt | 파이프를 구조재에 고정하는 U자형 볼트. FEM에서 RBE2로 모델링 |
| SpatialHash | 3D 공간을 격자 셀로 분할하는 공간 탐색 자료구조 |
| UnionFind | Disjoint Set Union. 연결 그룹 O(α(N)) 관리 |
| Canonical Node | Node Equivalence 병합 그룹의 대표 노드 (최소 ID) |
| Convergence Loop | 교차 분할 후 새 교차 발생 가능성으로 수렴까지 반복하는 루프 |
| Fixture | `samples/hitess_mini/`의 테스트용 소규모 CSV |
| Golden Test | 기대 출력 파일과 실제 출력을 비교하는 회귀 테스트 |
| Phase A/B/C | 구현 3단계: A=파싱, B=원본 모델 출력, C=알고리즘 파이프라인 |
| Stage | 파이프라인의 단일 처리 단위 (`IPipelineStage` 구현체) |
| Diagnostic | 파이프라인 실행 중 발생하는 Info/Warn/Error 진단 메시지 |
