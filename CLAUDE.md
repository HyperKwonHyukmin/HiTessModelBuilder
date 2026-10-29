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
| `FreeEndRbeAxialConeCos` | 0.8 | 자유단 부재 바깥 축 방향 원뿔(≈37°) — 이 안의 후보를 "축 방향 받침 부재"로 본다 |
| `FreeEndRbeAxialPreferenceMm` | 100.0 mm | 축 방향 후보가 최근접보다 이 값 이내로만 멀면 축 방향 선택(0 = 순수 최근접) |
| `RigidProximityMergeMm` | 50.0 mm | 인접 RBE2 클러스터 병합 임계 (HiTess `RigidProximityMergeModifier`와 동일) |
| `GroupConnectNoiseLengthMm` | 500.0 mm | GroupConnect 노이즈 단일 element 삭제 길이 임계 |
| `GroupConnectNoiseMaxDimMm` | 100.0 mm | GroupConnect 노이즈 단일 element 단면 최대 치수 임계 |
| `BoxUboltMaxSearchMm` | 2000.0 mm | BoxUbolt 4방향 후보 검색 최대 반경 |
| `BoxUboltAxialSafetyMm` | 10.0 mm | BoxUbolt exclusion zone axial 여유 |
| `PointMassReattachMaxDistMm` | 500.0 mm | 공중 PointMass 노드 → 최근접 pipe 노드 검색 최대 거리 |
| `GroupConnectTranslateMaxMm` | 10.0 mm | 이 간격 이하(CAD 오차)만 그룹 병진 이동, 초과는 제자리에서 `GROUP_CONNECT_RBE` 로 연결 |
| `GroupConnectMaxIterations` | 30 | ConnectivityConvergence 의 GroupConnect 반복 상한(RBE 연결은 한 겹씩 붙어 반복이 더 든다) |


**자유단 축 방향 우선 (2026-09-23, 3496-35210-A508372 N291)**: 수직 기둥 하단이 옆으로 76mm 떨어진 앵글 대신
바로 아래 H빔(87mm, 상부 플랜지 윗면 = 자유단 높이)에 붙게 한다. 조건은 ① 축 방향 원뿔 안 ② 최근접 + 선호 여유 이내
③ 최근접 부재에 **닿아 있지 않음**(거리 > 그 단면 최대치수/2) ④ 축 방향 부재 단면에 **얹혀 있음**(거리 ≤ 단면/2 + ExtraMargin).
③·④ 없이는 10개 실측 세트에서 6mm 로 닿은 옆 부재를 103mm 아래 부재로 바꾸는 오탐이 나왔다.
이렇게 만든 RBE 는 `DetailRemark = "AXIAL"` 이고, 두 후속 단계가 이 표식을 본다:
`LoopCloseProximityStage` 는 그 자유단을 다시 옆 부재와 묶지 않고, `RigidSharedNodeMergeStage` 는 병합 시 그 RBE 를
master 로 둔다(받침 부재 노드가 indep). 표식을 일반 FREE_END_RBE 전체로 넓히면 loop-close 수십 건이 사라진다(실측).

**apply-edit-intent 의 SPC 승계 (2026-09-23)**: 기준 BDF 의 SPC1 노드를 이어받을 때 편집 후 모델의 **RBE2 종속 노드는 뺀다**
(`EditIntentApplier.ReadSpcNodeIdsFromBdf`, build-full 시딩과 같은 규칙). 빠지면 편집으로 SPC 노드를 RBE 로 이었을 때
Nastran FATAL 2101(UM·US 중복)이 난다 — GMU 샘플 사다리 하단 4개 노드 자동 연결에서 24건 실측.
⚠ SPC 는 **최종 산출물 `<designName>.bdf` 에만** 있다. phase 파일(`06_Validation`)을 기준으로 편집하면 edited BDF 에 SPC1 이 0개다.


### FE 모델 공학 검토 반영 (2026-09-23)

CSV → BDF 변환을 구조 FE 관점에서 재검토하고 고친 것. 회귀 기준 = `csv/` 14세트(01~09, KangSangHoonReview, S10~S13)를
WorkBench 기본값(`--mesh-size 200 --ubolt-full-fix`) + `--run-nastran` 으로 돌려 **FATAL 0 · AUTOSPC 0 · 자중=반력** 유지.
(부분 자유도 U-bolt 모드 = `--ubolt-full-fix` 없이는 **수정 전부터 14세트 모두 FATAL 9050** — 배관 축방향 메커니즘.)

- **CSV `ori` = 단면 폭 방향(요소 z축)** 으로 해석해 CBEAM v = ori × x̂ 로 쓴다(`BeamOrientation.FromSectionWidthDirection`).
  과거엔 `BdfWriter`/`FinalValidation` 이 모든 요소를 기본 규칙(수평 v=Z, 수직 v=Y)으로 덮어써 ori 가 버려졌다
  (평행 v → Nastran FATAL 회피용). 근거: 샘플 전수에서 ori 는 항상 축에 수직이고, 수평 H 보의 ori 는 전부 수평 →
  이 해석이면 수평 H 보 웹이 수직으로 **기존과 같고**, 수직 기둥·수직 FBAR 의 ±Y 경우만 90° 바로잡힌다.
  ori 를 곧장 v 로 쓰면 수평 H 보가 전부 눕는다. 출력은 `BeamOrientation.Resolve` — 저장 v 가 축과 평행(|cos|≥0.9)
  하거나 0 이면 기본 규칙. 저장값이 UnitZ(배관·구 JSON)면 기본 규칙과 **비트 단위로 같다**. ROD/TUBE 는 ori 무시.
  ⚠ L(앵글)은 다리가 놓이는 사분면(부호)을 CSV 정의로 확정하지 못했다 — 도면 1건으로 확인 필요.
- **ATTA(배관 부착물) 질량 반영** — 과거 `> 10 t` 만 넣어 사실상 전부 빠졌는데 입력 감사는 'converted' 였다.
  배관 절점 500mm 밖 ATTA 는 `POINTMASS_REATTACH_UNRESOLVED` 후 OrphanNodeRemove 가 모델에서 뺀다(경고 문구에 명시).
- **배관↔구조 가짜 강결합 차단** — Intersection 은 배관↔구조 교차를 분할하지 않고, Extend 는 구조 끝을 배관까지 늘리지 않는다.
- **GroupConnect master = 구조 요소 최다 그룹** — 요소 수로만 고르면 배관 네트워크가 master 가 되어 구조 그룹이
  배관 쪽으로 붙었다(07: 이동 0건 → 39건 정상 연결).
- **GroupConnect 는 10mm 초과 간격을 병진 이동 대신 `GROUP_CONNECT_RBE`(RBE2)** 로 잇는다 — 그룹 전체를 최대 수백 mm
  옮겨 COG·러그 위치·편심이 바뀌던 문제. 대상 선택에도 FreeEnd 와 같은 **축 방향 우선**을 적용하고, 받침 부재가
  아직 master 가 아니면(같은 반복에 함께 붙을 slave) 연결을 다음 반복으로 미룬다(N291 이 옆 앵글로 붙던 재발 방지).
  자유단이 이미 RBE 종속이면 역할을 뒤집어 종속 중복을 피한다(안 그러면 병합으로 표식이 사라져 매 반복 재연결).
  RBE 노드는 LoopClose 후보에서 빠지고, RedundantBridge 화이트리스트에 있다.
- **SPC 바닥 선정**(`SpcSeeding.ResolveModelBottomNodes`) — 최저 Z+500 띠 안 **구조** 절점 중 더 아래로 이어지는
  구조 부재가 없는 높이 묶음(기둥 발·바닥 프레임)만. 구조 요소 20% 이상 덩어리에 고정점이 없으면 그 덩어리 자기
  바닥에도 적용(07/09: 최저점이 5.7m 아래 배관 받침대라 주 구조가 배관에 매달려 변위 3m/23m → 18mm/75mm).
  과거엔 띠 안 모든 절점(03: 380개 중 배관 148개)을 고정했다. 떨어진 그룹 공중 고정은 `SPC_GROUP_COVERAGE` 경고.
- **CollinearNodeMerge 덩어리 상한** — 쌍마다 50mm 여도 사슬로 이어 50mm 넘게 붕괴하지 않게(가까운 쌍부터 AABB 검사).
- **BDF 정밀도** — CONM2 질량·PBEAML 치수는 8자 안 최대 유효숫자(`BdfField.FormatPrecise`). 과거 질량 kg 반올림, 치수 0.1mm.
- **출력 요청** `MPCFORCES = ALL`(U-bolt 반력), `PARAM,GRDPNT,0`(무게 요약). `--run-nastran` 후 자중·반력 합력과
  AUTOSPC 개수를 출력하고 어긋나면 `NASTRAN_LOAD_IMBALANCE`/`NASTRAN_AUTOSPC` 경고.
- **보류: RBE 병합 시 cm 합집합** — 기본(`--ubolt-full-fix`)은 출력 cm 이 어차피 전부 123456 이라 결과 영향이 없고,
  부분 자유도 모드는 위와 같이 9050 이라 해석 자체가 안 된다. 병합 규칙만 바꾸면 기본 모드 연결성만 흔들린다 →
  U-bolt 자유도 모델링(배관 run 별 축방향 고정점)과 함께 설계할 것.

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
