# ClaudeModelBuilder 구현 플랜

## Context (왜 이 작업을 하는가)

AM(Aveva Marine) 3D 설계의 Beam류(H, L, Rod, Tube 등) 부재를 FEM 1D 유한요소 beam으로 변환하여 Nastran BDF를 생성하는 CLI 도구를 만든다. 3D에서는 용접이 면, 부재 교차점에서 Node 공유가 없고, 분리된 구조 그룹·배관 U-bolt RBE 같은 고도 기하 알고리즘이 필요하다.

레거시 프로젝트 `HiTessModelBuilder_26_01`(단일 프로젝트 ~70파일, 0 테스트, 문자열 `ExtraData` 남용, 하드코딩 tolerance)이 있으나 **원본은 수정하지 않고 읽기 전용 참조**로만 사용한다. 참조 프로젝트를 그대로 따라가지 않고 **Claude Code가 판단하여 유용한 알고리즘만 선별**하며, 부족하다고 판단되면 새로운 접근을 도입한다.

핵심 원칙: **각 단계마다 모델 상태를 JSON으로 덤프**하여 외부 도구(시각화)로 사용자가 직접 검증할 수 있게 한다. 사용자가 각 단계 결과를 확인·수정한 후 다음 단계로 진행하는 점진적 빌드 방식.

---

## 사용자 확정 결정

| 항목 | 결정 |
|---|---|
| 입력 포맷 | HiTess와 동일한 CSV (structure / pipe / equip 3종) |
| 코드 전략 | 선별 참조 + 새 구조. 참조에 얽매이지 않음 |
| UI | 콘솔 CLI |
| 프레임워크 | .NET 8 (필요 시 기하 라이브러리 단계적 도입) |
| 진행 방식 | **3 Phase 분할**, Phase 내 단계마다 JSON 덤프 후 사용자 확인 |
| 현재 상태 | `C:\Coding\ClaudeModelBuilder`는 `.claude/` 설정만 있는 빈 디렉토리 |
| 참조 경로 | `C:\Coding\Csharp\Projects\HiTessModelBuilder_26_01\HiTessModelBuilder_26_01` (읽기 전용) |

### 3-Phase 구조
- **Phase A**: CSV 파싱 — CSV → RawDesignData → JSON 덤프
- **Phase B**: 원본 모델 출력 — RawDesignData → FeModel → JSON 덤프 + 초기 BDF 출력
- **Phase C**: 알고리즘 단계별 적용 — 스테이지마다 FeModel JSON 덤프, 사용자 시각화 후 승인 → 다음 단계

---

## 솔루션 구조

```
C:\Coding\ClaudeModelBuilder\
├── ClaudeModelBuilder.sln
├── Directory.Build.props         # Nullable=enable, LangVersion=latest, TreatWarningsAsErrors=true
├── .editorconfig
├── .gitignore
├── src\
│   ├── Cmb.Core\                 # classlib: 도메인 모델, 기하 primitive, JSON 직렬화
│   ├── Cmb.Io\                   # classlib: CSV Reader, Nastran BDF Writer, JSON Dumper
│   ├── Cmb.Pipeline\             # classlib: Stage, Inspector, Modifier, Spatial
│   └── Cmb.Cli\                  # exe: Program.cs, System.CommandLine
├── tests\
│   ├── Cmb.Core.Tests\
│   ├── Cmb.Io.Tests\
│   ├── Cmb.Pipeline.Tests\
│   └── Cmb.Integration.Tests\
├── samples\
│   └── hitess_mini\              # 3~5행 축소 fixture CSV (시각화 검증용 소규모)
├── out\                          # CLI 실행 산출물 (gitignore)
│   ├── raw\                      # Phase A raw JSON
│   ├── model\                    # Phase B initial FeModel JSON + BDF
│   └── stages\                   # Phase C stage-by-stage FeModel JSON
└── docs\
    ├── architecture.md
    └── json-schema.md            # 시각화 도구 연동용 JSON 스키마
```

**NuGet 패키지**
- `System.CommandLine` — CLI 파싱
- `System.Text.Json` (BCL) — JSON 덤프 (Source Generator로 AOT 호환)
- `Microsoft.Extensions.Logging.Abstractions` + `.Console`
- `xUnit`, `FluentAssertions`, `Verify.Xunit`
- 기하 라이브러리는 초기 미도입. SVD 등 필요 시 `MathNet.Numerics` 추가

---

## 도메인 모델

- `Cmb.Core.Geometry` — `Point3`, `Vector3`, `Segment3` (`readonly struct`, `double`, `IEquatable`)
- `Cmb.Core.Model` — `Node`, `BeamElement`, `RigidElement`, `PointMass`, `NodeTags [Flags]`
- `Cmb.Core.Model.Sections` — `BeamSectionKind { H, L, Rod, Tube, Box, Channel, Bar }`, `BeamSection` record
- `Cmb.Core.Model.Categorization` — `EntityCategory { Structure, Pipe, Equipment }` (HiTess 문자열 ExtraData 강타입화)
- `Cmb.Core.Model.Context` — `FeModel`, `IdAllocator`
- `Cmb.Core.Serialization` — `FeModelJson`, `RawDesignDataJson` (System.Text.Json source-gen)
- `Cmb.Io.Csv` / `Cmb.Io.Nastran` / `Cmb.Io.Json`
- `Cmb.Pipeline` — `IPipelineStage`, `PipelineRunner`, `StageContext`, `RunOptions`, `PipelineReport`, `Diagnostic`
- `Cmb.Pipeline.Spatial` — `SpatialHash`, `ElementSpatialHash`, `UnionFind`, `ProjectionUtils`

**HiTess 대비 개선**
1. `ExtraData: Dictionary<string,string>` 남용 제거 → `EntityCategory` enum + `NodeTags [Flags]`
2. Tolerance 중앙화 — `RunOptions.Tolerances` 단일 레코드
3. Inspector/Modifier 인터페이스 분리 (컴파일 타임 mutation 방지)
4. **JSON 덤프가 first-class** — `FeModel.ToJson()`, `FeModel.FromJson()` 직접 지원
5. 수렴 loop 최대 반복 횟수 명시

---

## JSON 출력 전략 (핵심)

모든 Phase에서 동일한 `fe-model.schema.json` 사용. 외부 시각화 도구는 단일 스키마만 알면 됨.

### 스키마 (요약)
```json
{
  "meta": { "phase": "B|C-stage03", "stageName": "IntersectionStage", "timestamp": "...", "unit": "mm" },
  "nodes": [
    { "id": 1, "x": 0.0, "y": 0.0, "z": 0.0, "tags": ["Weld","ForcedSpc"] }
  ],
  "elements": [
    { "id": 1, "type": "BEAM", "startNode": 1, "endNode": 2, "propertyId": 10, "category": "Structure", "orientation": [0,0,1] }
  ],
  "rigids": [
    { "id": 1, "independentNode": 5, "dependentNodes": [6,7,8], "remark": "UBOLT" }
  ],
  "properties": [ { "id": 10, "kind": "H", "dims": [400,200,10,16], "materialId": 1 } ],
  "materials": [ { "id": 1, "name": "Steel", "E": 206000, "nu": 0.3, "rho": 7.85e-09 } ],
  "pointMasses": [ ... ],
  "diagnostics": [ { "severity": "Warn", "code": "SHORT_BEAM", "msg": "...", "elemId": 42 } ]
}
```

### 출력 위치
- Phase A: `out/raw/{inputName}.raw.json` (RawDesignData)
- Phase B: `out/model/{inputName}.initial.json` + `out/model/{inputName}.initial.bdf`
- Phase C: `out/stages/{inputName}/{idx:D2}_{stageName}.json` (+ 선택적 BDF)

### CLI 플래그
- `--dump-json` (기본 on) — 각 단계 JSON 덤프
- `--dump-bdf-per-stage` — 스테이지별 BDF도 저장 (Nastran 실행 검증용)
- `--stopat <stageName|index>` — 특정 단계까지만 실행

---

## Phase A: CSV 파싱 (4단계)

**목표**: CSV → `RawDesignData` DTO. JSON 덤프로 파싱 정확도 검증.

### A-1. 솔루션 스켈톤
- **산출물**: `.sln`, 4 src + 4 tests 프로젝트, `Directory.Build.props`, `.editorconfig`, `.gitignore`
- **검증**: `dotnet build`, `dotnet test` 그린
- **JSON 출력**: 없음

### A-2. 기하 primitive + 도메인 뼈대 + JSON 직렬화 기반
- **산출물**:
  - `Cmb.Core/Geometry/*` (Point3, Vector3, Segment3)
  - `Cmb.Core/Model/*` (Node, BeamElement, RigidElement, PointMass, NodeTags, EntityCategory, BeamSection, MaterialRef, PropertyRef)
  - `Cmb.Core/Model/Context/FeModel.cs`, `IdAllocator.cs`
  - `Cmb.Core/Serialization/FeModelJson.cs` — `ToJson()` / `FromJson()` (System.Text.Json source-gen)
  - `docs/json-schema.md` 1차 초안
  - 20+ unit tests (round-trip, JSON serialize/deserialize)
- **참조**: `Model/Geometry/GeometryTypes.cs`, `Model/Entities/Nodes.cs`, `Elements.cs`, `FeModelContext.cs`
- **JSON 출력**: 빈 FeModel 직렬화 smoke test

### A-3. HiTess CSV 리더
- **산출물**:
  - `Cmb.Io/Csv/CsvSchema.cs` (HiTess `CsvParser.cs` 426 LoC 분석 후 컬럼 상수화)
  - `Cmb.Io/Csv/HiTessStructureCsvReader.cs`, `HiTessPipeCsvReader.cs`, `HiTessEquipCsvReader.cs`
  - `Cmb.Io/Csv/CsvDesignLoader.cs` — 3 리더 조합 → `RawDesignData` 강타입 DTO
  - `samples/hitess_mini/*.csv` (3~5행 fixture)
- **JSON 출력**: `RawDesignData.ToJson()` → `out/raw/*.raw.json`
- **검증**: fixture 파싱 스냅샷 (Verify), 사용자가 JSON을 외부 도구로 시각화하여 좌표 검증

### A-4. CLI `parse` 서브커맨드
- **산출물**: `cmb parse --input <folder> --output out/raw/` → RawDesignData JSON만 출력
- **검증**: 샘플 CSV → `out/raw/*.raw.json` 생성, 레코드 수/좌표 육안 확인 가능
- **사용자 체크포인트**: 📍 CSV 파싱 결과 JSON 검증 후 Phase B 승인

---

## Phase B: 원본 모델 출력 (3단계)

**목표**: RawDesignData → FeModel(정리 전) → Nastran BDF + JSON 덤프. 알고리즘 적용 전의 "있는 그대로" 모델을 확인.

### B-1. FeModelBuilder (Raw → Model)
- **산출물**:
  - `Cmb.Core/Building/FeModelBuilder.cs` — raw rows → Nodes/Properties/Materials/BeamElements
  - **기본 Material**: Steel, E=206000 MPa, ν=0.3, ρ=7.85e-09 ton/mm³ (HiTess 동일, **mm/MPa 단위계 확정**)
  - **단면 매핑**: Ang→L, Beam→H, Bsc→CHAN, Bulb/Fbar→BAR, Rbar→ROD, Tube→TUBE
  - Property dedupe (shape + dims 해시)
- **참조**: `Services/Builders/RawFeModelBuilder.cs` (257 LoC), `PipeModelBuilder.cs` (323 LoC)
- **검증**: 샘플 CSV → FeModel 노드·엘리먼트 수 기대값

### B-2. Nastran BDF Writer
- **산출물**:
  - `Cmb.Io/Nastran/BdfField.cs` — 8-col 고정폭 + 과학표기 clamp
  - `Cmb.Io/Nastran/BdfWriter.cs` — GRID / CBEAM / PBEAML / PBARL / MAT1 / RBE2 / CONM2 / SPC1 / ENDDATA
  - 골든 테스트 (Verify 스냅샷)
- **참조**: `Exporter/BdfFormatFields.cs`, `BdfBuilder.cs` (350 LoC)
- **검증**: 2-node beam 최소 BDF 문자열 완전 일치

### B-3. CLI `build-raw` 서브커맨드
- **산출물**: `cmb build-raw --input <folder> --output out/model/` → CSV → FeModel → JSON + BDF 동시 출력 (알고리즘 적용 안 함)
- **JSON 출력**: `out/model/{input}.initial.json`
- **BDF 출력**: `out/model/{input}.initial.bdf`
- **사용자 체크포인트**: 📍 원본 FeModel 시각화로 부재 배치 육안 검증. Nastran 실행 시 FATAL 예상(노드 미공유 등)이지만 BDF 문법 자체는 통과해야 함. 승인 후 Phase C.

---

## Phase C: 알고리즘 단계별 적용 (6~8 스테이지)

**목표**: 각 스테이지 후 FeModel을 JSON으로 덤프 → 사용자 시각화 → 승인 → 다음 스테이지. 참조 프로젝트의 알고리즘 중 Claude Code가 판단해 유용한 것만 선별 구현.

**인프라 먼저 구축 (C-0)**:
- `IPipelineStage`, `PipelineRunner`, `StageContext`, `RunOptions`, `Diagnostic`
- 스테이지 완료 hook → 자동 JSON 덤프
- `cmb build-full --input <folder> --stopat <stageName>` 플래그
- 공간 인덱스 / UnionFind / Projection 유틸
- 수렴 루프 (MaxConvergenceIterations=50, 변경 수 0 종료)

**각 스테이지 구현 시 판단 기준** (참조에 얽매이지 않음):
- 참조 프로젝트에 해당 기능이 있고 품질 좋음 → 개념 포팅
- 참조에 있으나 설계 부채 큼 → 새로 설계 (예: `ExtraData` 제거)
- 참조에 없으나 필요 → 새 알고리즘 도입
- **Claude Code가 각 스테이지 진입 시 "이 스테이지에서 필요한 정리/변형은 무엇인가" 재평가**하고 구현 항목 제안 → 사용자 확인 후 진행

### 초기 제안 스테이지 (사용자 피드백에 따라 조정)

#### C-1. SanityPreprocessStage
- duplicate beam 제거, 0-length 엘리먼트 제거, collinear 3-node 병합
- Diagnostic으로 제거 내역 리포트
- **참조**: `ElementDuplicateInspector.cs`, `ElementDetectShortInspector.cs`, `CollinearNodeMergeModifier.cs`, `StructuralSanityInspector.cs`
- **Claude Code 판단**: Sanity 항목 중 실제 유용한 것만 채택. 참조가 가진 7개 검사 전부가 아니라 초기엔 3~4개로 시작하고 필요 시 추가

#### C-2. MeshingStage
- 긴 beam을 `MaxElementLength` 기준으로 분할
- pipe/struct 각각 다른 기준
- **Claude Code 판단**: HiTess는 복잡한 meshing이 있으나 초기엔 단순 등분할로 시작. 필요 시 곡선 대응 추가

#### C-3. NodeEquivalenceStage
- 근접 노드 tolerance 병합 (sweep-and-prune O(N log N))
- **참조**: `Pipeline/NodeInspector/InspectEquivalenceNodes.cs`
- **이유**: 모든 후속 알고리즘의 전제조건

#### C-4. IntersectionStage
- segment-segment 교차점에서 split + node share (Dan Sunday closest-points)
- 평행선 skip, tolerance 내 교차만 인정
- 수렴 loop (split → 재검사)
- **참조**: `ElementIntersectionSplitModifier.cs`, `ElementSplitByExistingNodesModifier.cs`

#### C-5. WeldNodeStage
- CSV weld 힌트(`entity.Weld = "start"/"end"`) 기반 노드에 `NodeTags.Weld` 부여
- 필요 시 node forced share
- **참조**: `RawFeModelBuilder.cs:115` 주변 Weld 처리

#### C-6. GroupConnectStage
- UnionFind 연결그룹 산출
- 최대 그룹을 master로, 나머지를 translate/extend로 최대 그룹에 붙임
- **참조**: `ElementGroupTranslationModifier.cs`, `ElementExtendToIntersectModifier.cs`, `ElementConnectivityInspector.cs`
- **Claude Code 판단**: 참조의 "intent vector" 기반 pre-clustering이 과잉일 수 있음. 단순 거리 기반으로 시작 후 필요 시 개선

#### C-7. UboltRbeStage
- pipe 카테고리에서 U-bolt 감지 (CSV 힌트 `Type="UBOLT"`)
- 2-phase search (작은 반경 → 큰 반경)로 구조체에 snap
- snap 성공 시 RBE2 생성, 실패 시 `NodeTags.ForcedSpc` → SPC1로 export
- **참조**: `UboltSnapToStructureModifier.cs`, `UboltBoxConnectionModifier.cs`, `ElementRbeConnectionModifier.cs`, `UboltExclusionZones.cs`

#### C-8. FinalValidationStage + ExportStage
- 최종 validity 체크 (자유단, orphan component, missing property 등)
- `BdfWriter`로 최종 BDF 출력
- 옵션: Nastran 외부 실행 + F06 FATAL 스캔 (`NastranExecutionService` 개념 참조)
- **참조**: `StructuralSanityInspector.cs` 전체

### 각 스테이지별 사용자 체크포인트 (반복)
1. Claude Code가 해당 스테이지 구현 제안 (참조 요약 + 채택/변형 설명)
2. 사용자 승인 후 구현 + 단위 테스트
3. `cmb build-full --stopat C-N` 실행 → `out/stages/{idx}_{name}.json` 생성
4. 사용자가 외부 시각화 도구로 JSON 검증
5. 이슈 발견 시 사용자 수정 지시 → 반영
6. 승인 시 다음 스테이지

---

## 재사용할 참조 파일 (읽기 전용)

**핵심 알고리즘 (개념 참조 대상)**
- `Pipeline/FeModelProcessPipeline.cs` (363 LoC) — 수렴 루프 패턴
- `Pipeline/Utils/SpatialHash.cs`, `ElementSpatialHash.cs`, `UnionFind.cs`, `ProjectionUtils.cs`
- `Pipeline/NodeInspector/InspectEquivalenceNodes.cs` — sweep-and-prune
- `Pipeline/ElementModifier/ElementIntersectionSplitModifier.cs` — Dan Sunday 교차
- `Pipeline/ElementModifier/ElementGroupTranslationModifier.cs` — 그룹 확장
- `Pipeline/ElementModifier/UboltSnapToStructureModifier.cs` — 2-phase snap
- `Services/Builders/RawFeModelBuilder.cs` — 단면 매핑, 기본 Material
- `Exporter/BdfFormatFields.cs` — 8-col 고정폭 포맷터
- `Exporter/BdfBuilder.cs` — BDF 섹션 구성
- `Parsers/CsvParser.cs` — CSV 컬럼 및 prefix 분류

**참조하지 않을 요소 (명시적 배제 결정)**
- `ExtraData: Dictionary<string,string>` 패턴 — 강타입 대체
- `Console.WriteLine` 기반 로깅 — `ILogger` 대체
- 단일 프로젝트 구조 — 4-프로젝트 분할
- 하드코딩된 tolerance — `RunOptions.Tolerances` 집중화
- 0 테스트 정책 — xUnit + 골든 테스트

---

## 검증 전략

### Phase A/B: 파싱·빌드 정확도
- xUnit 단위 테스트 (round-trip, 포맷)
- 샘플 CSV → 레코드 수/좌표 스냅샷
- JSON 시각화로 육안 검증 (사용자)

### Phase C: 스테이지별 검증
- 각 스테이지 JSON 덤프 → 사용자 시각화 체크포인트
- 수치 테스트: "+" 교차 2-beam → 4-beam + 1 공유노드 등 Verify 골든
- 통합 테스트: HiTess 실제 CSV 1~2개 투입 → `BdfComparer`로 노드 좌표 1e-6, CBEAM 위상 동형, RBE2 dependent 집합 동일성 검증

### 성능
- 10k 엘리먼트 end-to-end < 10s
- `PipelineReport`에 스테이지별 타이머

---

## 리스크 및 대응

| 리스크 | 대응 |
|---|---|
| 좌표계/단위 암묵 | `FeModel.LengthUnit = Millimeter` 명시, JSON `meta.unit`에 기록 |
| Tolerance 하드코딩 산재 | `RunOptions.Tolerances` 단일 레코드 집중화 |
| 수렴 루프 무한 반복 | `MaxConvergenceIterations=50` + 변경 수 0 체크 |
| BDF 지수표기 clamp 미묘 | HiTess 포맷터 line-by-line 포팅 후 1만 값 diff 0 보장 |
| CSV 포맷 drift | `CsvSchema`에 버전 감지 |
| U-bolt CSV 힌트 의존 | CSV 없을 시 근접도 휴리스틱 Phase 2 확장 |
| 스테이지 구현 선별 시 과소/과잉 | 사용자와 각 스테이지 시작 시 협의, JSON 덤프로 즉시 검증 |
| JSON 스키마 변경 시 시각화 도구 break | `docs/json-schema.md`에 버전 + `meta.schemaVersion` 필드 |

---

## 진행 방식 (Phase 간·Phase 내)

### Phase 간
- Phase 완료 시 사용자 승인 → 다음 Phase
- 필요 시 사용자가 중간 코드 직접 수정 가능 (Claude Code는 수정사항을 읽고 다음 단계 반영)

### Phase 내 단계
1. Claude Code: 단계 시작 전 구현 계획 간략 제시 (무엇을 만들고 참조에서 어떤 개념을 가져오는지)
2. 사용자 승인
3. Claude Code: 구현 + 단위 테스트
4. `dotnet build` + `dotnet test` 그린 확인
5. CLI 실행 → JSON 덤프 생성
6. 사용자: 외부 시각화 도구로 검증
7. 이슈 발견 시 지시 → Claude Code 수정
8. 승인 → 다음 단계

### 원본 참조 보호
- `C:\Coding\Csharp\Projects\HiTessModelBuilder_26_01` 는 **Read 도구로만 열람**
- 절대 Edit/Write 하지 않음
- CLAUDE.md에 이 규칙 명시 (Phase A-1에서 프로젝트 CLAUDE.md 작성 시 포함)
