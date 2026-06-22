# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

HiTessModelBuilder는 AM(Aveva Marine) 3D 설계 CSV → FEM 1D Beam → Nastran BDF 변환 CLI 도구 (`cmb`)이다. .NET 8, C# 12.

**레거시 참조 프로젝트** (`C:\Coding\Csharp\Projects\HiTessModelBuilder_26_01\HiTessModelBuilder_26_01`): **읽기 전용 참조만 허용. 절대 수정 금지.**

---

## 빌드 및 테스트

```bash
# 전체 빌드
dotnet build HiTessModelBuilder.sln

# 전체 테스트
dotnet test HiTessModelBuilder.sln

# 단일 테스트 프로젝트
dotnet test tests/Cmb.Core.Tests

# 단일 테스트 (필터)
dotnet test tests/Cmb.Core.Tests --filter "FullyQualifiedName~Point3Tests"

# CLI 실행
dotnet run --project src/Cmb.Cli -- parse --input samples/hitess_mini --output out/raw
dotnet run --project src/Cmb.Cli -- build-raw --input samples/hitess_mini --output out/model
dotnet run --project src/Cmb.Cli -- build-full --input samples/hitess_mini --output out/stages --stopat NodeEquivalenceStage
```

---

## 솔루션 구조

```
HiTessModelBuilder.sln
Directory.Build.props          ← Nullable=enable, TreatWarningsAsErrors=true, LangVersion=latest
src/
  Cmb.Core/       도메인 모델, 기하 primitive, JSON 직렬화 (외부 NuGet 의존 없음, BCL만)
  Cmb.Io/         CSV Reader (HiTess 3종), BDF Writer, JSON Dumper
  Cmb.Pipeline/   IPipelineStage, PipelineRunner, Spatial 자료구조
  Cmb.Cli/        System.CommandLine, 3개 서브커맨드
tests/
  Cmb.Core.Tests / Cmb.Io.Tests / Cmb.Pipeline.Tests / Cmb.Integration.Tests
samples/hitess_mini/  3~5행 fixture CSV (파싱 검증용)
out/              CLI 산출물 (gitignore)
docs/             PRD.md, ROADMAP.md, architecture.md, json-schema.md
```

**의존성 방향**: `Cmb.Cli → Cmb.Pipeline → Cmb.Io → Cmb.Core`. 순환 참조 금지.

---

## 핵심 도메인 개념

### 기하 Primitive (`Cmb.Core.Geometry`)
`Point3`, `Vector3`, `Segment3` — `readonly struct`, `double` 좌표, `IEquatable<T>`. 단위: **mm**.

### FEM 엔티티 (`Cmb.Core.Model`)
- `Node`: `int Id`, `Point3 Position`, `NodeTags Tags`
- `BeamElement`: `int Id`, `StartNodeId`, `EndNodeId`, `PropertyId`, `EntityCategory`, `Vector3 Orientation`
- `RigidElement`: `IndependentNodeId`, `DependentNodeIds` — Nastran RBE2
- `PointMass`: U-bolt 또는 Equipment 집중 질량
- `NodeTags [Flags]`: `None=0 | Weld=1 | Intersection=2 | Merged=4 | Boundary=8 | Spc=16` — `Spc`는 최종 BDF SPC1에 포함된 노드. final JSON뿐 아니라 phase JSON도 후처리로 동일하게 반영됨.
- `EntityCategory`: `Structure | Pipe | Equipment`
- `BeamSectionKind`: `H | L | Rod | Tube | Box | Channel | Bar`

### FeModel (`Cmb.Core.Model.Context`)
모든 엔티티의 컨테이너. `IdAllocator`로 단조 증가 ID 발급. `LengthUnit = "mm"` 고정.

### JSON 직렬화 (`Cmb.Core.Serialization`)
`System.Text.Json` Source Generator (AOT 호환). `FeModel.ToJson()` / `FeModel.FromJson()`.

---

## Phase별 CLI 출력

| Phase | 커맨드 | 출력 경로 | 목적 |
|-------|--------|-----------|------|
| A | `cmb parse` | `out/raw/*.raw.json` | CSV 파싱 결과 검증 |
| B | `cmb build-raw` | `out/model/*.initial.json` + `*.bdf` | 알고리즘 적용 전 원본 모델 |
| C | `cmb build-full` | `out/stages/{idx:D2}_{name}.json` | 스테이지별 변환 결과 |

JSON `meta` 필드: `phase`, `stageName`, `stageIndex`, `processingDurationMs`, `timestamp`, `unit="mm"`, `schemaVersion="1.2"`.

**스테이지별 선택 필드** (옵션, null이면 JSON에서 생략): `connectivity`, `healthMetrics`, `diagnostics[]`, `stageDeltas`, `validationResult` (FinalValidation 전용), `stageHistory` (마지막 스테이지 전용).

---

## Pipeline 스테이지 순서 (Phase C)

18개 sub-stage가 6개 **phase**로 묶여 출력됨. 기본 출력: `{idx:D2}_{phaseName}.{json|bdf}`. 디버깅 시 `--verbose-dump` → `detail/` 하위에 sub-stage별 추가 dump.

| Phase (출력 파일) | Sub-stages (실행 순서) |
|-------------------|----------------------|
| `01_Preprocess` | SanityPreprocess → **Meshing** |
| `02_ElementSplit` | NodeEquivalence → Intersection → **DanglingShortRemove** |
| `03_NodeMerge` | **CollinearNodeMerge** |
| `04_Connectivity` | ExtendToIntersect → Split → GroupConnect → Split → BoxUboltConnection → **Split** |
| `05_RigidPostProc` | PointMassReattach → RigidProximityMerge → FreeEndRbe → **Split** |
| `06_Validation` | UboltAnchorVerticalize → OrphanNodeRemove → **FinalValidation** |

- `SplitByExistingNodes`는 4번 실행됨 (ExtendToIntersect, GroupConnect, BoxUboltConnection, FreeEndRbe 후속 처리).
- `UboltRbeStage`는 `PreconnectInitialUbolts()` 헬퍼에서 pipeline 시작 전 1회 실행 (별도 dump 없음).
- `BoxUboltConnection`은 BOX 단면 U-bolt RBE2의 dependent를 owner pipe local frame(top/bottom/left/right) 4방향 가장 가까운 구조 부재로 연결.
- `PointMassReattach`는 element/rigid에 참조되지 않는 공중 노드에 붙은 PointMass(CONM2)를 `PointMassReattachMaxDistMm` 반경 내 가장 가까운 pipe 노드로 재연결 후 고립 노드 제거.
- `RigidProximityMerge`는 50mm 이내 RBE2를 단일 RBE2로 통합 (HiTess `RigidProximityMergeModifier` 이식).
- `RigidElementNormalizer.MergeSharedDependentNodes`는 GroupConnect/BoxUbolt 후 같은 dep를 공유하는 RBE2를 1개로 통합 (cm 자유도는 union, PointMass>UBOLT>shared-dep 우선순위로 master 선택, 기본 cm은 `"123456"` — 부분 자유도 보존이 필요하면 `RigidElementNormalizer.cs:58-60` 정책 검토).
- `OrphanNodeRemove`는 element/rigid(indep+dep)/PointMass 어디에도 참조되지 않는 노드를 모두 삭제하고 `TraceAction.NodeRemoved` 로 lifecycle 추적. Boundary 태그가 있는 노드는 SPC seed 보존을 위해 제외.

Phase 경계 테이블 (`Program.cs:phaseBoundaries`)은 0-based stage index로 관리 → stage 추가 시 해당 index에 entry 추가.

### 핵심 Tolerance (`RunOptions.Tolerances`)

| 필드 | 기본값 | 용도 |
|------|--------|------|
| `NodeMergeMm` | 1.0 mm | 동일 노드 판정 |
| `IntersectionSnapMm` | 5.0 mm | 교차점 snap |
| `UboltSnapMaxDistMm` | 300.0 mm | U-bolt snap 기본 검색 반경 (HiTess `MaxSearchRadius`와 동일) |
| `ShortElemMinMm` | 5.0 mm | dangling 단축 요소 제거 기준 |
| `SpatialCellSizeMm` | 200.0 mm | 공간 해시 셀 크기 |
| `MeshingMaxLengthStructure` | 2000.0 mm | Structure 분할 기준 |
| `MeshingMaxLengthPipe` | 1000.0 mm | Pipe 분할 기준 |
| `NodeOnSegmentTolMm` | 0.5 mm | 노드-세그먼트 on-segment 판정 |
| `DanglingShortLengthMm` | 50.0 mm | dangling 짧은 가지 제거 기준 |
| `CollinearMergeDistanceMm` | 50.0 mm | Collinear 노드 병합 거리 |
| `GroupConnectLocalTolMm` | 40.0 mm | Meta-group 구성 시 인접 slave 간 최대 거리 |
| `GroupConnectExtraMarginMm` | 50.0 mm | 단면 최대 치수 + 여유 = 병진 이동 허용 threshold |
| `ExtendExtraMarginMm` | 100.0 mm | 자유단 연장 시 단면 치수 외 여유 |
| `ExtendCoplanarTolMm` | 10.0 mm | ray-segment 공면 판정 상한 |
| `ExtendSnapLateralMm` | 2.0 mm | pSeg 모드 전환 임계 (설계 오차 흡수) |
| `ExtendObstacleTolMm` | 1.0 mm | extend 경로 obstacle 검출 거리 임계 |
| `ExtendMinElemLengthMm` | 5.0 mm | extend 후 stub 생성 방지 — 이동 후 elemA 최소 길이 |
| `ExtendMaxIterations` | 10 | ExtendToIntersect 수렴 루프 상한 |
| `MaxConvergenceIterations` | 50 | Intersection / DanglingShortRemove 수렴 루프 상한 |
| `FreeEndRbeExtraMarginMm` | 25.0 mm | 자유단 RBE 브릿지 interior projection 여유 |
| `FreeEndRbeEndpointMarginMm` | 5.0 mm | 자유단 RBE endpoint capture (tRaw≈0/≈1) 여유 |
| `RigidProximityMergeMm` | 50.0 mm | 인접 RBE2 클러스터 병합 임계 (HiTess `RigidProximityMergeModifier`와 동일) |
| `GroupConnectNoiseLengthMm` | 500.0 mm | GroupConnect 노이즈 단일 element 삭제 길이 임계 |
| `GroupConnectNoiseMaxDimMm` | 100.0 mm | GroupConnect 노이즈 단일 element 단면 최대 치수 임계 |
| `BoxUboltMaxSearchMm` | 2000.0 mm | BoxUbolt 4방향 후보 검색 최대 반경 |
| `BoxUboltAxialSafetyMm` | 10.0 mm | BoxUbolt exclusion zone axial 여유 |
| `PointMassReattachMaxDistMm` | 2000.0 mm | 공중 PointMass 노드 → 최근접 pipe 노드 검색 최대 거리 |

**Tolerance는 `RunOptions.Tolerances` 레코드에만 정의. 코드 내 하드코딩 금지.**

---

## HiTess 참조 가이드 (읽기 전용)

| 알고리즘 | 참조 파일 |
|----------|-----------|
| 수렴 루프 패턴 | `Pipeline/FeModelProcessPipeline.cs` |
| 공간 해시/UnionFind | `Pipeline/Utils/SpatialHash.cs`, `UnionFind.cs` |
| 노드 동일성 (sweep-and-prune) | `Pipeline/NodeInspector/InspectEquivalenceNodes.cs` |
| 교차 분할 (Dan Sunday) | `Pipeline/ElementModifier/ElementIntersectionSplitModifier.cs` |
| 그룹 연결 | `Pipeline/ElementModifier/ElementGroupTranslationModifier.cs` |
| U-bolt snap | `Pipeline/ElementModifier/UboltSnapToStructureModifier.cs` |
| 단면 매핑, 기본 재료 | `Services/Builders/RawFeModelBuilder.cs` |
| BDF 고정폭 포맷 | `Exporter/BdfFormatFields.cs`, `BdfBuilder.cs` |
| CSV 컬럼 구조 | `Parsers/CsvParser.cs` |

**HiTess에서 사용하지 않는 패턴**: `ExtraData: Dictionary<string,string>` (→ `EntityCategory`/`NodeTags`로 대체), `Console.WriteLine` 로깅 (→ `ILogger`), 하드코딩 tolerance.

---

## 테스트 전략

- **xUnit + FluentAssertions + Verify.Xunit** (골든 스냅샷 테스트)
- 기하 primitive: 단위 테스트 20개 이상
- BDF Writer: `Verify` 스냅샷으로 완전 일치
- 파이프라인 스테이지: `"+" 교차 2-beam → 4-beam + 공유노드 1개` 등 수치 케이스
- 통합: `samples/hitess_mini` CSV → BDF 왕복 검증

---

## NuGet 패키지 정책

- `Cmb.Core`: 외부 NuGet 없음 (`System.Text.Json` BCL만)
- 기하 라이브러리(`MathNet.Numerics`)는 SVD 등 실제 필요 시점에 `Cmb.Pipeline`에만 추가
- 추가 시 `Directory.Build.props`에 버전 고정
