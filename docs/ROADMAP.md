# ClaudeModelBuilder ROADMAP

> **참조 PRD**: `docs/PRD.md`
> **대상**: AM(Aveva Marine) 3D CSV → FEM 1D Beam → Nastran BDF 변환 CLI 도구 신규 작성
> **총 단계**: 11단계 (Phase A: 4단계, Phase B: 3단계, Phase C: 4단계) | **요구사항**: 18건 | **대상 모듈**: 25개+

---

## 전체 진행 현황

| 단계 | 이름 | 핵심 목표 | 포함 요구사항 | 상태 |
|------|------|-----------|---------------|------|
| Phase A-1 | 솔루션 뼈대 + 기하 Primitive | `dotnet build` 통과, `Point3`/`Vector3`/`Segment3` 구현 | FR-A001, FR-A002 | ✅ 완료 |
| Phase A-2 | 도메인 모델 + JSON 직렬화 | 핵심 엔티티 및 `FeModel` 직렬화 구현 | FR-A003, FR-A004 | ✅ 완료 |
| Phase A-3 | CSV 리더 구현 | HiTess 3종 CSV 파싱, `RawDesignData` 반환 | FR-A005 | ✅ 완료 |
| Phase A-4 | `cmb parse` CLI | Phase A 엔드포인트, `out/raw/*.raw.json` 생성 | FR-A006 | ✅ 완료 |
| Phase B-1 | FeModelBuilder + BDF Writer | `RawDesignData` → `FeModel` 변환 + BDF 출력 기반 | FR-B001, FR-B002 | ✅ 완료 |
| Phase B-2 | `cmb build-raw` CLI | Phase B 엔드포인트, `out/model/*.initial.json` + `*.bdf` 생성 | FR-B003 | ✅ 완료 |
| Phase B-3 | 추적성 + 파이프라인 재사용성 | SourceName·TraceLog·JSON v1.1·Cmb.Pipeline→Core 의존성 변경 | FR-B004, FR-B005 | ✅ 완료 |
| Phase C-1 | 파이프라인 인프라 | `IPipelineStage`, `PipelineRunner`, 공간 자료구조 구현 | FR-C000 | ⬜ 대기 |
| Phase C-2 | 전처리 3단계 Stage | Sanity → Meshing → NodeEquivalence 순차 구현 | FR-C001, FR-C002, FR-C003 | ⬜ 대기 |
| Phase C-3 | 교차·연결·RBE2 Stage | Intersection → WeldNode → GroupConnect → UboltRbe 순차 구현 | FR-C004, FR-C005, FR-C006, FR-C007 | ⬜ 대기 |
| Phase C-4 | 최종 검증 + `cmb build-full` CLI | FinalValidation + Export + Phase C 엔드포인트 | FR-C008, FR-C009 | ⬜ 대기 |

상태 표시: ⬜ 대기 / 🔄 진행 중 / ✅ 완료 / ⏸ 보류

---

## Phase A-1 — 솔루션 뼈대 + 기하 Primitive

**목표**: 솔루션 구조를 생성하고 모든 이후 단계의 좌표 연산 기반이 되는 `Point3`, `Vector3`, `Segment3`를 구현한다.
**선행 조건**: 없음 (시작점)

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `ClaudeModelBuilder.sln`, `Directory.Build.props`, 4개 src + 4개 tests 프로젝트 | FR-A001 | `.sln` 및 프로젝트 파일 생성, `Nullable=enable` / `TreatWarningsAsErrors=true` 전역 적용, 테스트 프로젝트가 대응 src 프로젝트를 참조하도록 설정 | ❌ (시작점) |
| `Cmb.Core/Geometry/Point3.cs` | FR-A002 | `readonly struct Point3 : IEquatable<Point3>` 구현. `X`, `Y`, `Z` 프로퍼티, `DistanceTo(Point3)`, `+`/`-` 연산자(Vector3 교차), 생성자에서 NaN/Infinity 검증 | ✅ (FR-A001 완료 후 병렬 가능) |
| `Cmb.Core/Geometry/Vector3.cs` | FR-A002 | `readonly struct Vector3 : IEquatable<Vector3>` 구현. `Length`, `Normalize()`, `Dot`, `Cross`, `+`/`-`/`*` 연산자, `static Zero` | ✅ (FR-A001 완료 후 병렬 가능) |
| `Cmb.Core/Geometry/Segment3.cs` | FR-A002 | `readonly struct Segment3` 구현. `Start`/`End`, `Length`, `Midpoint`, `ClosestPointTo(Point3)` (t ∈ [0,1] clamp), `DistanceTo(Point3)` | ✅ (FR-A001 완료 후 병렬 가능) |
| `Cmb.Core.Tests/Geometry/Point3Tests.cs` 외 | FR-A002 | `Point3`, `Vector3`, `Segment3` 단위 테스트 20개 이상 작성 (xUnit + FluentAssertions) | ✅ (구현과 병렬 가능) |

### 체크리스트

- [ ] FR-A001: `dotnet build` 경고 0, 오류 0 확인
- [ ] FR-A001: `Nullable=enable`, `TreatWarningsAsErrors=true`가 `Directory.Build.props`에 전역 적용됨
- [ ] FR-A001: 각 테스트 프로젝트가 대응 src 프로젝트를 프로젝트 참조함
- [ ] FR-A002: `Point3`, `Vector3`, `Segment3` 모두 `readonly struct`, `double` 좌표, `IEquatable<T>` 구현
- [ ] FR-A002: `Point3` — X, Y, Z 프로퍼티, `DistanceTo(Point3)`, `+`/`-` 연산자 (Vector3와 교차)
- [ ] FR-A002: `Vector3` — X, Y, Z, `Length`, `Normalize()`, Dot/Cross 곱, `+`/`-`/`*` 연산자
- [ ] FR-A002: `Segment3` — Start/End (`Point3`), `Length`, `Midpoint`, `ClosestPointTo(Point3)`
- [ ] FR-A002: 단위 테스트 20개 이상 통과
- [ ] Phase A-1 통합 검증: `dotnet test Cmb.Core.Tests` 전체 통과

---

## Phase A-2 — 도메인 모델 + JSON 직렬화

**목표**: FEM 엔티티 전체(`Node`, `BeamElement`, `RigidElement`, `PointMass`, `BeamSection`, `Material`)와 `FeModel` 컨테이너 및 JSON 직렬화를 구현한다. 이후 모든 단계의 데이터 구조 기반이 된다.
**선행 조건**: Phase A-1 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Core/Model/Node.cs` | FR-A003 | `sealed class Node` — `int Id`, `Point3 Position`, `NodeTags Tags` (setter 없음, 변경 불가 초기화) | ✅ |
| `Cmb.Core/Model/BeamElement.cs` | FR-A003 | `sealed class BeamElement` — `Id`, `StartNodeId`, `EndNodeId`, `PropertyId`, `EntityCategory Category`, `Vector3 Orientation` | ✅ |
| `Cmb.Core/Model/RigidElement.cs` | FR-A003 | `sealed class RigidElement` — `Id`, `IndependentNodeId`, `IReadOnlyList<int> DependentNodeIds`, `string Remark` | ✅ |
| `Cmb.Core/Model/PointMass.cs` | FR-A003 | `sealed class PointMass` — `Id`, `NodeId`, `double Mass` | ✅ |
| `Cmb.Core/Model/Enums.cs` | FR-A003 | `[Flags] enum NodeTags` (None=0, Weld=1, Intersection=2, Merged=4, Boundary=8), `enum BeamSectionKind` (H, L, Rod, Tube, Box, Channel, Bar), `enum EntityCategory` (Structure, Pipe, Equipment) | ✅ |
| `Cmb.Core/Model/BeamSection.cs` | FR-A003 | `record BeamSection(int Id, BeamSectionKind Kind, double[] Dims, int MaterialId)`. Kind별 Dims 레이아웃은 PRD 5.2 준수 | ✅ |
| `Cmb.Core/Model/Material.cs` | FR-A003 | `record Material(int Id, string Name, double E, double Nu, double Rho)`. 기본값: `new Material(1, "Steel", 206000.0, 0.3, 7.85e-9)` | ✅ |
| `Cmb.Core/Model/IdAllocator.cs` | FR-A003 | `IdAllocator` — `int Next()` 순차 발급, 단일 스레드 가정 | ✅ |
| `Cmb.Core/Model/Context/FeModel.cs` | FR-A004 | `FeModel` — `List<Node>`, `List<BeamElement>`, `List<RigidElement>`, `List<PointMass>`, `List<BeamSection>`, `List<Material>`, `string LengthUnit = "mm"` | ❌ (FR-A003 완료 후) |
| `Cmb.Core/Serialization/FeModelJson.cs` | FR-A004 | `System.Text.Json` Source Generator 사용, AOT 호환. `FeModel.ToJson()` → UTF-8 문자열, `FeModel.FromJson(string)` → `FeModel`. JSON 필드명은 PRD 6.1 스키마와 1:1 매핑 | ❌ (FeModel 완료 후) |
| `Cmb.Core.Tests/Serialization/FeModelJsonTests.cs` | FR-A004 | 왕복 테스트 (직렬화 → 역직렬화 → 동치) 통과 | ❌ (직렬화 구현 후) |

### 체크리스트

- [ ] FR-A003: `Node` — `int Id`, `Point3 Position`, `NodeTags Tags` (setter 없음, 변경 불가 초기화)
- [ ] FR-A003: `BeamElement` — `Id`, `StartNodeId`, `EndNodeId`, `PropertyId`, `EntityCategory`, `Vector3 Orientation`
- [ ] FR-A003: `RigidElement` — `Id`, `IndependentNodeId`, `IReadOnlyList<int> DependentNodeIds`, `string Remark`
- [ ] FR-A003: `PointMass` — `Id`, `NodeId`, `double Mass`
- [ ] FR-A003: `NodeTags` — `[Flags] enum` (None=0, Weld=1, Intersection=2, Merged=4, Boundary=8)
- [ ] FR-A003: `BeamSectionKind` — enum { H, L, Rod, Tube, Box, Channel, Bar }
- [ ] FR-A003: `EntityCategory` — enum { Structure, Pipe, Equipment }
- [ ] FR-A003: `BeamSection` — `record` (Id, Kind, Dims, MaterialId)
- [ ] FR-A003: `IdAllocator` — `int Next()`, 스레드 안전 불필요 (단일 스레드 파이프라인)
- [ ] FR-A004: `FeModel` — List<Node>, List<BeamElement>, List<RigidElement>, List<PointMass>, List<BeamSection>, List<Material>, `LengthUnit` (상수 `"mm"`)
- [ ] FR-A004: `FeModelJson` — `System.Text.Json` Source Generator 사용, AOT 호환
- [ ] FR-A004: `FeModel.ToJson()` → UTF-8 문자열 반환
- [ ] FR-A004: `FeModel.FromJson(string)` → `FeModel` 반환
- [ ] FR-A004: JSON 스키마 섹션 6에 정의된 필드명과 1:1 매핑
- [ ] FR-A004: 왕복 테스트 (직렬화 → 역직렬화 → 동치) 통과
- [ ] Phase A-2 통합 검증: `dotnet test Cmb.Core.Tests` 전체 통과

---

## Phase A-3 — HiTess CSV 리더 구현

**목표**: AM CSV 3종(Structure/Pipe/Equipment)을 파싱하여 `RawDesignData`를 반환하는 리더를 구현하고, fixture 기반 스냅샷 테스트를 통과시킨다.
**선행 조건**: Phase A-2 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `samples/hitess_mini/` | FR-A005 | Structure/Pipe/Equipment 각 3~5행 fixture CSV 파일 작성 (테스트 기준 데이터) | ✅ |
| `Cmb.Io/Csv/RawRows.cs` | FR-A005 | `RawBeamRow`, `RawPipeRow`, `RawEquipRow` DTO 정의. `RawDesignData` — `IReadOnlyList<RawBeamRow>`, `IReadOnlyList<RawPipeRow>`, `IReadOnlyList<RawEquipRow>` | ✅ |
| `Cmb.Io/Csv/HiTessStructureCsvReader.cs` | FR-A005 | Structure 행 파싱 (컬럼: NodeA, NodeB, ProfilType, Dims, Category 등). 헤더 행 자동 스킵, 빈 행 무시 | ✅ |
| `Cmb.Io/Csv/HiTessPipeCsvReader.cs` | FR-A005 | Pipe 행 파싱 (컬럼: NodeA, NodeB, OD, WT, Weld 힌트 등). 헤더 행 자동 스킵, 빈 행 무시 | ✅ |
| `Cmb.Io/Csv/HiTessEquipCsvReader.cs` | FR-A005 | Equipment 행 파싱 (컬럼: NodeId, Mass, Cog 등). 헤더 행 자동 스킵, 빈 행 무시 | ✅ |
| `Cmb.Io/Csv/CsvDesignLoader.cs` | FR-A005 | 3종 리더를 조합하여 `RawDesignData` 반환 | ❌ (3종 리더 완료 후) |
| `Cmb.Io.Tests/Csv/HiTessStructureCsvReaderTests.cs` 외 | FR-A005 | `samples/hitess_mini` fixture 기반 Verify.Xunit 스냅샷 테스트 작성 및 통과 | ❌ (리더 구현 후) |

### 체크리스트

- [ ] FR-A005: `HiTessStructureCsvReader` — Structure 행 파싱 (NodeA, NodeB, ProfilType, Dims, Category 등)
- [ ] FR-A005: `HiTessPipeCsvReader` — Pipe 행 파싱 (NodeA, NodeB, OD, WT, Weld 힌트 등)
- [ ] FR-A005: `HiTessEquipCsvReader` — Equipment 행 파싱 (NodeId, Mass, Cog 등)
- [ ] FR-A005: `CsvDesignLoader` — 3종 리더 조합, `RawDesignData` 반환
- [ ] FR-A005: `RawDesignData` — `IReadOnlyList<RawBeamRow>`, `IReadOnlyList<RawPipeRow>`, `IReadOnlyList<RawEquipRow>`
- [ ] FR-A005: 헤더 행 자동 스킵, 빈 행 무시
- [ ] FR-A005: `samples/hitess_mini` fixture 기반 Verify.Xunit 스냅샷 테스트 통과
- [ ] Phase A-3 통합 검증: `dotnet test Cmb.Io.Tests` 전체 통과

---

## Phase A-4 — `cmb parse` CLI 서브커맨드

**목표**: `cmb parse` 명령으로 Phase A 전체를 끝에서 끝까지 실행하고 `out/raw/*.raw.json`을 생성한다. 사용자 체크포인트 A에서 JSON을 시각화 도구로 검증한다.
**선행 조건**: Phase A-3 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Cli/Program.cs` | FR-A006 | `System.CommandLine` 루트 커맨드 `cmb` 초기화. `--version`, `--help` 지원 | ✅ |
| `Cmb.Cli/Commands/ParseCommand.cs` | FR-A006 | `cmb parse --input <folder> --output <dir>` 구현. `--input` 폴더 내 CSV 파일 자동 탐색 (Structure/Pipe/Equipment 구분), `CsvDesignLoader` 호출, `out/raw/*.raw.json` 생성 | ❌ (Program.cs 초기화 후) |
| `Cmb.Io/Json/RawDesignDataDumper.cs` | FR-A006 | `out/raw/*.raw.json` 직렬화. meta 포함: `phase="A"`, `stageName="parse"`, `timestamp`, `unit="mm"`, `schemaVersion="1.0"` | ✅ |
| `Cmb.Integration.Tests/ParseCommandTests.cs` | FR-A006 | `cmb parse` end-to-end 통합 테스트. 입력 폴더 없을 경우 오류 메시지 + exit code 1 검증 | ❌ (CLI 구현 후) |

### 체크리스트

- [ ] FR-A006: `cmb parse --input <folder> --output <dir>` 실행 가능
- [ ] FR-A006: `--input` 폴더 내 CSV 파일 자동 탐색 (Structure/Pipe/Equipment 구분)
- [ ] FR-A006: `out/raw/*.raw.json` 생성
- [ ] FR-A006: JSON meta 포함 — `phase="A"`, `stageName="parse"`, `timestamp`, `unit="mm"`, `schemaVersion="1.0"`
- [ ] FR-A006: 입력 폴더 없을 경우 오류 메시지 + exit code 1 반환
- [ ] FR-A006: `--help` 출력 정상 동작
- [ ] Phase A-4 통합 검증: `dotnet test Cmb.Integration.Tests` Phase A 관련 테스트 통과

---

> **체크포인트 A**: `out/raw` JSON을 외부 시각화 도구로 검증 후 Phase B 진행

---

## Phase B-1 — FeModelBuilder + BDF Writer

**목표**: `RawDesignData`를 알고리즘 없이 `FeModel`로 변환하는 `FeModelBuilder`와 Nastran BDF 카드 출력을 담당하는 `BdfWriter`를 구현한다. 두 모듈은 서로 독립적으로 병렬 개발 가능하다.
**선행 조건**: Phase A-4 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Pipeline/Build/FeModelBuilder.cs` | FR-B001 | `RawDesignData` → `FeModel` 변환. 각 `RawBeamRow` → `BeamElement` + 양 끝점 `Node`, `BeamSection` 중복 제거(동일 Kind+Dims → 단일 PropertyId), `Material` 기본값 자동 생성, `IdAllocator` 사용 | ✅ |
| `Cmb.Pipeline/Build/EquipmentMapper.cs` | FR-B001 | `RawEquipRow` → `PointMass` 변환 | ✅ |
| `Cmb.Pipeline/Build/PipeMapper.cs` | FR-B001 | `RawPipeRow` → `BeamElement(Category=Pipe)` 변환, `BeamSectionKind.Tube` 사용 | ✅ |
| `Cmb.Io/Nastran/BdfField.cs` | FR-B002 | 8열 고정폭 포맷터. 정수 우측 정렬, 실수 소수점 우선 / 8열 초과 시 지수 표기(대문자 E), NaN/Infinity → `ArgumentException`, 빈 필드 → 8개 공백 (HiTess `BdfFormatFields` 로직 참조) | ✅ |
| `Cmb.Io/Nastran/BdfWriter.cs` | FR-B002 | GRID, CBEAM, PBEAML, PBARL, MAT1, RBE2, CONM2, SPC1, ENDDATA 카드 출력. 출력 파일이 Nastran free-field 파서로 재파싱 가능 (왕복 검증) | ❌ (BdfField 완료 후) |
| `Cmb.Core.Tests/Build/FeModelBuilderTests.cs` | FR-B001 | `BeamSection` 중복 제거, `Material` 기본값, `IdAllocator` 발급 단위 테스트 | ✅ |
| `Cmb.Io.Tests/Nastran/BdfFieldTests.cs` | FR-B002 | `BdfField` 포맷터 단위 테스트 10개 이상 (정수, 실수 소수점, 지수, NaN, 빈 필드 등) | ✅ |
| `Cmb.Io.Tests/Nastran/BdfWriterTests.cs` | FR-B002 | Verify.Xunit 스냅샷으로 BDF 출력 1건 검증 | ❌ (BdfWriter 완료 후) |

### 체크리스트

- [ ] FR-B001: 각 `RawBeamRow` → `BeamElement` + 양 끝점 `Node` 생성
- [ ] FR-B001: `BeamSection` 중복 제거(Dedupe): 동일 `Kind + Dims` 조합은 단일 `PropertyId` 공유
- [ ] FR-B001: `Material` 기본값 자동 생성 — Steel E=206000 MPa, ν=0.3, ρ=7.85e-9 ton/mm³
- [ ] FR-B001: 모든 `Node.Id`, `BeamElement.Id`, `BeamSection.Id`는 `IdAllocator`에서 발급
- [ ] FR-B001: 장비(`RawEquipRow`) → `PointMass` 변환
- [ ] FR-B001: 파이프(`RawPipeRow`) → `BeamElement(Category=Pipe)` 변환, `BeamSectionKind.Tube` 사용
- [ ] FR-B002: `BdfField` — 8열 고정폭 포맷터, 실수 지수 표기 clamp (HiTess `BdfFormatFields` 로직 참조)
- [ ] FR-B002: `BdfWriter` — GRID, CBEAM, PBEAML, PBARL, MAT1, RBE2, CONM2, SPC1, ENDDATA 카드 출력
- [ ] FR-B002: 출력 파일이 Nastran free-field 파서로 재파싱 가능 (왕복 검증)
- [ ] Phase B-1 통합 검증: `dotnet test Cmb.Io.Tests Cmb.Pipeline.Tests` Phase B 관련 테스트 통과

---

## Phase B-2 — `cmb build-raw` CLI 서브커맨드

**목표**: `cmb build-raw` 명령으로 Phase B 전체를 실행하고 `out/model/*.initial.json` + `*.bdf`를 생성한다. 사용자 체크포인트 B에서 JSON + BDF를 검증한다.
**선행 조건**: Phase B-1 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Cli/Commands/BuildRawCommand.cs` | FR-B003 | `cmb build-raw --input <folder> --output <dir>` 구현. `FeModelBuilder` 호출, `out/model/*.initial.json` + `*.bdf` 생성. BDF 첫 줄에 SOL/CEND 없이 벌크 데이터만 포함 | ❌ (시작점) |
| `Cmb.Io/Json/FeModelDumper.cs` | FR-B003 | `out/model/*.initial.json` 직렬화. meta: `phase="B"`, `stageName="initial"`, `unit="mm"`, `schemaVersion="1.0"` | ✅ |
| `Cmb.Integration.Tests/BuildRawCommandTests.cs` | FR-B003 | `cmb build-raw` end-to-end 통합 테스트. `*.initial.json` + `*.bdf` 생성 검증 | ❌ (CLI 구현 후) |

### 체크리스트

- [ ] FR-B003: `cmb build-raw --input <folder> --output <dir>` 실행 가능
- [ ] FR-B003: `out/model/*.initial.json` + `*.bdf` 생성
- [ ] FR-B003: JSON meta — `phase="B"`, `stageName="initial"`, `unit="mm"`, `schemaVersion="1.0"`
- [ ] FR-B003: BDF 파일 첫 줄에 SOL/CEND 없이 벌크 데이터만 포함 (검증 도구 호환)
- [ ] Phase B-2 통합 검증: `dotnet test Cmb.Integration.Tests` Phase B 관련 테스트 통과

---

> **체크포인트 B**: `out/model` JSON + BDF를 시각화 및 Nastran 문법 검증 후 Phase C 진행

---

## Phase B-3 — 추적성(Traceability) + 파이프라인 재사용성

**목표**: CSV 행의 SourceName을 FE 엔티티에 연결하고, 파이프라인 변환 이력을 TraceLog로 기록한다. `Cmb.Pipeline`이 `Cmb.Io`에 독립적으로 분리되어 다른 프로젝트에서 파이프라인을 재사용할 수 있게 한다.
**선행 조건**: Phase B-2 완료 ✅

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Core/Model/TraceEvent.cs` (신규) | FR-B004 | `TraceAction` enum + `TraceEvent` sealed record (Action, StageName, ElementId?, NodeId?, RelatedElementId?, RelatedNodeId?, Note?) | ✅ |
| `Cmb.Core/Model/BeamElement.cs` | FR-B004 | `string? SourceName`, `int? ParentElementId` 옵셔널 생성자 파라미터 추가 | ✅ |
| `Cmb.Core/Model/PointMass.cs` | FR-B004 | `string? SourceName` 옵셔널 생성자 파라미터 추가 | ✅ |
| `Cmb.Core/Model/RigidElement.cs` | FR-B004 | `string? SourceName` 옵셔널 생성자 파라미터 추가 | ✅ |
| `Cmb.Core/Model/Context/FeModel.cs` | FR-B004 | `List<TraceEvent> TraceLog`, `AddTrace()` 메서드 추가 | ❌ (TraceEvent 완료 후) |
| `Cmb.Core/Building/FeModelBuilder.cs` | FR-B004 | `sourceName: row.Name` 전달 + `TraceAction.ElementCreated` 이벤트 기록 | ❌ (FeModel 완료 후) |
| `Cmb.Core/Serialization/FeModelJson.cs` | FR-B004 | DTO에 sourceName?, parentElementId?, TraceEventDto, trace? 배열 추가. SchemaVersion = "1.1" | ❌ (모델 완료 후) |
| `Cmb.Pipeline/Cmb.Pipeline.csproj` | FR-B005 | `<ProjectReference Cmb.Io>` → `<ProjectReference Cmb.Core>`. `MEL.Abstractions 8.0.0` 추가 | ✅ |
| `Cmb.Pipeline/Core/RunOptions.cs` (신규) | FR-B005 | `Tolerances` + `RunOptions` 레코드 (StopAfterStage, RecordTrace) | ✅ |
| `Cmb.Pipeline/Core/Diagnostic.cs` (신규) | FR-B005 | `DiagnosticSeverity` enum + `Diagnostic` record | ✅ |
| `Cmb.Pipeline/Core/IPipelineStage.cs` (신규) | FR-B005 | `string Name` + `bool Execute(StageContext ctx)` | ✅ |
| `Cmb.Pipeline/Core/StageContext.cs` (신규) | FR-B005 | FeModel + RunOptions + ILogger + Diagnostics. RecordTrace 게이트 | ❌ (IPipelineStage 완료 후) |
| `Cmb.Pipeline/Core/PipelineReport.cs` (신규) | FR-B005 | `StageReport` + `PipelineReport` 클래스 | ✅ |
| `Cmb.Pipeline/Core/PipelineRunner.cs` (신규) | FR-B005 | `static Run(...)` — Stage 순차 실행, StopAfterStage, 예외 처리 | ❌ (StageContext 완료 후) |
| `Cmb.Core.Tests/Model/TraceabilityTests.cs` (신규) | FR-B004 | TraceEvent 동등성, FeModel.AddTrace, SourceName 기본값 null | ✅ |
| `Cmb.Core.Tests/Building/FeModelBuilderTraceTests.cs` (신규) | FR-B004 | Build 후 Element.SourceName = row.Name, TraceLog 이벤트 수 | ❌ (빌더 완료 후) |
| `Cmb.Core.Tests/Serialization/FeModelJsonTraceTests.cs` (신규) | FR-B004 | sourceName round-trip, trace 배열 직렬화, trace 없으면 키 자체 없음 | ❌ (JSON 완료 후) |
| `Cmb.Pipeline.Tests/Core/PipelineRunnerTests.cs` (신규) | FR-B005 | 빈 스테이지, 통과/실패/예외 스테이지, StopAfterStage, RecordTrace=false | ❌ (Runner 완료 후) |

### 체크리스트

- [ ] FR-B004: `TraceEvent` 레코드 + `TraceAction` 열거 신규 생성
- [ ] FR-B004: `BeamElement.SourceName?`, `BeamElement.ParentElementId?` 추가 (기존 테스트 그린)
- [ ] FR-B004: `PointMass.SourceName?`, `RigidElement.SourceName?` 추가
- [ ] FR-B004: `FeModel.TraceLog` + `AddTrace()` 추가
- [ ] FR-B004: `FeModelBuilder`에서 `sourceName` 전달 및 `ElementCreated` 이벤트 기록
- [ ] FR-B004: JSON 스키마 v1.1 — `sourceName?`, `parentElementId?`, `trace?` 필드 추가
- [ ] FR-B004: `trace` 비어있으면 JSON 키 생략 확인
- [ ] FR-B005: `Cmb.Pipeline.csproj` — Cmb.Io 참조 제거, Cmb.Core 참조로 변경
- [ ] FR-B005: `RunOptions`, `Tolerances`, `Diagnostic`, `IPipelineStage` 신규 생성
- [ ] FR-B005: `StageContext`, `PipelineReport`, `PipelineRunner` 신규 생성
- [ ] FR-B005: `PipelineRunner.Run(onStageComplete?)` 콜백 동작 확인
- [ ] Phase B-3 통합 검증: `dotnet build` 경고 0, `dotnet test` 전체 통과

---

## Phase C-1 — 파이프라인 인프라

**목표**: 모든 Stage가 의존하는 추상화(`IPipelineStage`, `PipelineRunner`, `SpatialHash<T>`, `UnionFind`, `ProjectionUtils`)를 구현한다.
**선행 조건**: Phase B-2 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Pipeline/Core/IPipelineStage.cs` | FR-C000 | `IPipelineStage` 인터페이스 — `string Name`, `int StageIndex`, `Task<StageResult> RunAsync(StageContext)` | ✅ |
| `Cmb.Pipeline/Core/IModelInspector.cs` | FR-C000 | `IModelInspector` — `FeModel`을 받아 진단 목록 반환 (mutation 금지, 컴파일 타임 강제) | ✅ |
| `Cmb.Pipeline/Core/IModelModifier.cs` | FR-C000 | `IModelModifier` — `FeModel`을 받아 변형 후 반환 | ✅ |
| `Cmb.Pipeline/Core/StageContext.cs` | FR-C000 | `StageContext` — `FeModel`, `RunOptions`, `PipelineReport`, `ILogger` | ✅ |
| `Cmb.Pipeline/Core/RunOptions.cs` | FR-C000 | `RunOptions` — `Tolerances` 레코드 포함. `NodeMergeTol=1.0`, `IntersectionTol=0.1`, `UboltSnapMaxDist=50.0`, `ShortElemMin=5.0`, `SpatialCellSizeMm=200.0`, `MaxConvergenceIterations=50`, `MeshingMaxLengthStructure=2000.0`, `MeshingMaxLengthPipe=1000.0` | ✅ |
| `Cmb.Pipeline/Core/PipelineReport.cs` | FR-C000 | `PipelineReport` — `List<Diagnostic>` 누적. `Diagnostic` — `DiagnosticSeverity Severity`, `string Code`, `string Message`, `int? ElemId`, `int? NodeId` | ✅ |
| `Cmb.Pipeline/Core/PipelineRunner.cs` | FR-C000 | 스테이지 순차 실행, `--stopat` 지원, 각 스테이지 후 JSON 덤프 (옵션). `ILogger<T>` 사용, Stage 시작/완료 로그 출력 | ❌ (인터페이스/컨텍스트 완료 후) |
| `Cmb.Pipeline/Spatial/SpatialHash.cs` | FR-C000 | `SpatialHash<T>` — 셀 크기 파라미터화, `Insert`, `QueryNeighbors` | ✅ |
| `Cmb.Pipeline/Spatial/ElementSpatialHash.cs` | FR-C000 | `ElementSpatialHash` — `BeamElement` 전용 공간 해시 | ❌ (SpatialHash 완료 후) |
| `Cmb.Pipeline/Spatial/UnionFind.cs` | FR-C000 | `UnionFind` — `Union(a,b)`, `Find(a)`, `GetGroups()` | ✅ |
| `Cmb.Pipeline/Spatial/ProjectionUtils.cs` | FR-C000 | `ProjectionUtils` — `ClosestPointOnSegment`, `SegmentToSegmentDistance` (Dan Sunday 알고리즘) | ✅ |
| `Cmb.Pipeline.Tests/Spatial/UnionFindTests.cs` | FR-C000 | `UnionFind` 단위 테스트 10개 이상 | ✅ |
| `Cmb.Pipeline.Tests/Spatial/SpatialHashTests.cs` | FR-C000 | `SpatialHash<T>` 단위 테스트 10개 이상 | ✅ |

### 체크리스트

- [ ] FR-C000: `IPipelineStage` — `string Name`, `StageIndex`, `Task<StageResult> RunAsync(StageContext)`
- [ ] FR-C000: `IModelInspector` — `FeModel`을 받아 진단 목록 반환 (mutation 금지, 컴파일 타임 강제)
- [ ] FR-C000: `IModelModifier` — `FeModel`을 받아 변형 후 반환
- [ ] FR-C000: `PipelineRunner` — 스테이지 순차 실행, `--stopat` 지원, 각 스테이지 후 JSON 덤프
- [ ] FR-C000: `StageContext` — `FeModel`, `RunOptions`, `PipelineReport`, `ILogger`
- [ ] FR-C000: `RunOptions` — `Tolerances` 레코드 포함 (8개 필드 기본값 PRD 준수)
- [ ] FR-C000: `PipelineReport` — `List<Diagnostic>` 누적
- [ ] FR-C000: `Diagnostic` — `DiagnosticSeverity Severity`, `string Code`, `string Message`, `int? ElemId`, `int? NodeId`
- [ ] FR-C000: `DiagnosticSeverity` — Info, Warn, Error
- [ ] FR-C000: `SpatialHash<T>` — 셀 크기 파라미터화, `Insert`, `QueryNeighbors`
- [ ] FR-C000: `ElementSpatialHash` — `BeamElement` 전용 공간 해시
- [ ] FR-C000: `UnionFind` — `Union(a,b)`, `Find(a)`, `GetGroups()`
- [ ] FR-C000: `ProjectionUtils` — `ClosestPointOnSegment`, `SegmentToSegmentDistance`
- [ ] Phase C-1 통합 검증: `dotnet test Cmb.Pipeline.Tests` 인프라 관련 테스트 통과

---

## Phase C-2 — 전처리 3단계 Stage

**목표**: 파이프라인 Stage 1~3인 `SanityPreprocessStage`, `MeshingStage`, `NodeEquivalenceStage`를 구현한다. 각 Stage는 이전 Stage 완료를 전제로 한다.
**선행 조건**: Phase C-1 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Pipeline/Stages/SanityPreprocessStage.cs` | FR-C001 | 중복 요소 제거, 단축 요소 제거(`Length < ShortElemMin`), 공선 노드 병합(A-B-C에서 B가 AC 위에 있으면 B 제거), 노드 참조 정합성 검사. 진단 코드: `DUPLICATE_ELEM`, `SHORT_ELEM`, `COLLINEAR_NODE`, `ORPHAN_NODE_REF` | ✅ |
| `Cmb.Pipeline/Stages/MeshingStage.cs` | FR-C002 | Structure: `Length > MeshingMaxLengthStructure` 이면 `ceil(Length / MaxLength)` 등분할. Pipe: `Length > MeshingMaxLengthPipe` 이면 동일. Equipment 요소는 분할 안 함. 진단 코드: `MESHING_SPLIT` | ❌ (SanityPreprocessStage 완료 후) |
| `Cmb.Pipeline/Stages/NodeEquivalenceStage.cs` | FR-C003 | X 좌표 정렬 + 슬라이딩 윈도우 sweep-and-prune(O(N log N)). `UnionFind`로 병합 그룹 결정, 최소 ID canonical 선정, 요소 NodeId 교체, 루프 요소 제거. 진단 코드: `NODE_MERGED` | ❌ (MeshingStage 완료 후) |
| `Cmb.Pipeline.Tests/Stages/SanityPreprocessStageTests.cs` | FR-C001 | 최소 2개 단위 테스트 (중복 요소, 단축 요소, 공선 노드, 노드 참조 오류 케이스) | ✅ |
| `Cmb.Pipeline.Tests/Stages/MeshingStageTests.cs` | FR-C002 | 최소 2개 단위 테스트 (Structure 분할, Pipe 분할, Equipment 미분할) | ❌ (MeshingStage 구현 후) |
| `Cmb.Pipeline.Tests/Stages/NodeEquivalenceStageTests.cs` | FR-C003 | 최소 2개 단위 테스트 (근접 노드 병합, 루프 요소 제거) | ❌ (NodeEquivalenceStage 구현 후) |

### 체크리스트

- [ ] FR-C001: 중복 요소 제거 (동일 `StartNodeId`/`EndNodeId` 쌍)
- [ ] FR-C001: 단축 요소 제거 (`Length < Tolerances.ShortElemMin`)
- [ ] FR-C001: 공선 노드 병합 (세 노드 A-B-C에서 B가 AC 선분 위에 있으면 B 제거, A-C 직결)
- [ ] FR-C001: 노드 참조 정합성 검사 (존재하지 않는 `NodeId` 참조 시 `Error` 진단)
- [ ] FR-C001: 후조건 — 중복 없음, 단축 요소 없음, 모든 요소-노드 참조 유효
- [ ] FR-C001: 진단 코드 — `DUPLICATE_ELEM`, `SHORT_ELEM`, `COLLINEAR_NODE`, `ORPHAN_NODE_REF`
- [ ] FR-C002: `Category=Structure` — `Length > MeshingMaxLengthStructure` 이면 `ceil(Length / MeshingMaxLengthStructure)` 등분할
- [ ] FR-C002: `Category=Pipe` — `Length > MeshingMaxLengthPipe` 이면 동일 방식 등분할
- [ ] FR-C002: 분할 시 중간 노드 생성 (`IdAllocator.Next()`), 원래 요소 제거 후 분할 요소 삽입
- [ ] FR-C002: `Equipment` 요소는 분할하지 않음
- [ ] FR-C002: 후조건 — 모든 Structure 요소 `Length <= MeshingMaxLengthStructure`, 모든 Pipe 요소 `Length <= MeshingMaxLengthPipe`
- [ ] FR-C002: 진단 코드 — `MESHING_SPLIT`
- [ ] FR-C003: 노드를 X 좌표로 정렬, 슬라이딩 윈도우 sweep-and-prune (O(N log N))
- [ ] FR-C003: `UnionFind`로 병합 그룹 결정, 최소 ID 노드를 canonical 선정
- [ ] FR-C003: 모든 `BeamElement`, `RigidElement`의 노드 ID를 canonical 노드 ID로 교체
- [ ] FR-C003: 루프 요소(StartNodeId == EndNodeId 된 것) 제거
- [ ] FR-C003: 후조건 — 거리 `< NodeMergeTol`인 노드 쌍 없음
- [ ] FR-C003: 진단 코드 — `NODE_MERGED`
- [ ] Phase C-2 통합 검증: `--stopat NodeEquivalenceStage`로 파이프라인 실행 후 `out/stages/03_NodeEquivalence.json` 생성 확인

---

## Phase C-3 — 교차·연결·RBE2 Stage

**목표**: 파이프라인 Stage 4~7인 `IntersectionStage`, `WeldNodeStage`, `GroupConnectStage`, `UboltRbeStage`를 구현한다. 각 Stage는 이전 Stage 완료를 전제로 한다.
**선행 조건**: Phase C-2 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Pipeline/Stages/IntersectionStage.cs` | FR-C004 | `ElementSpatialHash`로 후보 쌍 추출 → Dan Sunday `SegmentToSegmentDistance` → 거리 `< IntersectionTol`이면 교차점에 새 노드 삽입 + 요소 2분할. 수렴 루프 (`MaxConvergenceIterations` 상한). 진단 코드: `INTERSECTION_SPLIT`, `CONVERGENCE_LIMIT_REACHED` | ✅ |
| `Cmb.Pipeline/Stages/WeldNodeStage.cs` | FR-C005 | `RawPipeRow.IsWeld == true`인 행의 끝점 좌표와 가장 가까운 노드(거리 `< NodeMergeTol`)에 `NodeTags.Weld` 추가. 힌트 없으면 아무것도 하지 않음. 진단 코드: `WELD_TAG_APPLIED`, `WELD_HINT_UNMATCHED` | ❌ (IntersectionStage 완료 후) |
| `Cmb.Pipeline/Stages/GroupConnectStage.cs` | FR-C006 | `UnionFind`로 BeamElement 연결 그룹 식별 → 최대 그룹 선정 → 비최대 그룹에 대해 최대 그룹 최근접 요소 검색 → 거리 `< UboltSnapMaxDist * 2`이면 중간 연결 노드 생성 후 단축 빔 연결(`NodeTags.Boundary`). 연결 불가 그룹 → `Warn`. 진단 코드: `GROUP_CONNECTED`, `ISOLATED_GROUP` | ❌ (WeldNodeStage 완료 후) |
| `Cmb.Pipeline/Stages/UboltRbeStage.cs` | FR-C007 | Phase 1 힌트 기반 snap → Phase 2 근접도 fallback. Snap 성공: `RigidElement(Remark="UBOLT")` 생성. Snap 실패: `NodeTags.Boundary` + `Warn`. 진단 코드: `UBOLT_RBE2_CREATED`, `UBOLT_SNAP_FAILED` | ❌ (GroupConnectStage 완료 후) |
| `Cmb.Pipeline.Tests/Stages/IntersectionStageTests.cs` | FR-C004 | 최소 2개 단위 테스트 (교차 분할, 수렴 루프 상한) | ✅ |
| `Cmb.Pipeline.Tests/Stages/WeldNodeStageTests.cs` | FR-C005 | 최소 2개 단위 테스트 (용접 힌트 매칭, 힌트 없음) | ❌ (WeldNodeStage 구현 후) |
| `Cmb.Pipeline.Tests/Stages/GroupConnectStageTests.cs` | FR-C006 | 최소 2개 단위 테스트 (단일 그룹, 고립 그룹 Warn) | ❌ (GroupConnectStage 구현 후) |
| `Cmb.Pipeline.Tests/Stages/UboltRbeStageTests.cs` | FR-C007 | 최소 2개 단위 테스트 (힌트 기반 RBE2 생성, fallback Warn) | ❌ (UboltRbeStage 구현 후) |

### 체크리스트

- [ ] FR-C004: `ElementSpatialHash`로 후보 쌍 추출 (O(N²) 전수 탐색 금지)
- [ ] FR-C004: 두 선분 최근접점 계산, 거리 `< IntersectionTol` 이면 교차 판정
- [ ] FR-C004: 교차점에 새 노드 삽입, 두 요소를 각각 2개로 분할
- [ ] FR-C004: 수렴 루프, `MaxConvergenceIterations` 초과 시 `Warn` 후 종료
- [ ] FR-C004: 후조건 — 교차 쌍 없거나 `MaxConvergenceIterations` 도달
- [ ] FR-C004: 진단 코드 — `INTERSECTION_SPLIT`, `CONVERGENCE_LIMIT_REACHED`
- [ ] FR-C005: `RawPipeRow.IsWeld == true`인 끝점 좌표 → 가장 가까운 노드(거리 `< NodeMergeTol`)에 `NodeTags.Weld` 추가
- [ ] FR-C005: 힌트 없으면 아무것도 하지 않음 (경고 없음)
- [ ] FR-C005: 후조건 — 용접 힌트가 있는 노드에 `NodeTags.Weld` 적용됨
- [ ] FR-C005: 진단 코드 — `WELD_TAG_APPLIED`, `WELD_HINT_UNMATCHED`
- [ ] FR-C006: `UnionFind`로 `BeamElement` 연결 그룹 식별, 최대 그룹 선정
- [ ] FR-C006: 비최대 그룹에서 최대 그룹 최근접 요소 검색, 거리 `< UboltSnapMaxDist * 2`이면 중간 연결 노드 + 단축 빔(`NodeTags.Boundary`)
- [ ] FR-C006: 연결 불가한 고립 그룹은 `Warn` 진단 발행
- [ ] FR-C006: 후조건 — 단일 연결 그룹(또는 고립 그룹 Warn 발행)
- [ ] FR-C006: 진단 코드 — `GROUP_CONNECTED`, `ISOLATED_GROUP`
- [ ] FR-C007: Phase 1 (힌트 기반): `RawPipeRow` U-bolt 힌트 좌표 근처 Structure 요소에 snap
- [ ] FR-C007: Phase 2 (fallback): 힌트 없으면 모든 Pipe 요소 끝점에서 가장 가까운 Structure 노드, 거리 `< UboltSnapMaxDist`이면 snap
- [ ] FR-C007: Snap 성공 → `RigidElement(Remark="UBOLT")` 생성 (독립 노드=Structure, 종속 노드=Pipe)
- [ ] FR-C007: Snap 실패 → `NodeTags.Boundary` + `Warn`
- [ ] FR-C007: 후조건 — U-bolt 힌트가 있는 Pipe 끝점에 `RigidElement` 또는 `Boundary` 태그 적용
- [ ] FR-C007: 진단 코드 — `UBOLT_RBE2_CREATED`, `UBOLT_SNAP_FAILED`
- [ ] Phase C-3 통합 검증: `--stopat UboltRbeStage`로 파이프라인 실행 후 `out/stages/07_UboltRbe.json` 생성 확인

---

## Phase C-4 — 최종 검증 + `cmb build-full` CLI

**목표**: `FinalValidationStage`와 `ExportStage`로 파이프라인을 마무리하고, `cmb build-full` CLI를 완성한다. 전체 end-to-end 통합 테스트를 통과시킨다.
**선행 조건**: Phase C-3 완료

### 구현 대상

| 모듈/클래스 | 요구사항 ID | 작업 내용 | 병렬 가능 |
|-------------|-------------|-----------|-----------|
| `Cmb.Pipeline/Stages/FinalValidationStage.cs` | FR-C008 | 자유단 노드 검사(1개 요소에만 연결 + `NodeTags.Boundary` 없음 → `Warn`), 고아 노드 검사(어떤 요소에도 미참조 → `Warn`), 누락 Property 검사(`Error`), 누락 Material 검사(`Error`). `Error` 1개 이상이면 BDF 출력 차단 + exit code 2 | ❌ (시작점) |
| `Cmb.Pipeline/Stages/ExportStage.cs` | FR-C008 | 최종 BDF 파일 생성(`BdfWriter` 사용), 최종 JSON 덤프 (`out/stages/08_FinalValidation.json`) | ❌ (FinalValidationStage 완료 후) |
| `Cmb.Cli/Commands/BuildFullCommand.cs` | FR-C009 | `cmb build-full --input <folder> --output <dir> [--stopat] [--dump-json] [--dump-bdf-per-stage]` 구현. `PipelineRunner` 호출, `PipelineReport` 요약 stdout 출력. `Error` 진단 → exit code 2, 경고만 → exit code 0 | ❌ (ExportStage 완료 후) |
| `Cmb.Pipeline.Tests/Stages/FinalValidationStageTests.cs` | FR-C008 | 최소 2개 단위 테스트 (누락 Property Error, 자유단 노드 Warn) | ✅ |
| `Cmb.Integration.Tests/BuildFullCommandTests.cs` | FR-C009 | `cmb build-full` end-to-end 통합 테스트 3건 이상. `--stopat`, `--dump-json`, exit code 2 케이스 포함 | ❌ (CLI 구현 후) |

### 체크리스트

- [ ] FR-C008: 자유단 노드 검사 — 1개 요소에만 연결된 노드 중 `NodeTags.Boundary` 없는 것 → `Warn`
- [ ] FR-C008: 고아 노드 검사 — 어떤 요소에도 참조되지 않는 노드 → `Warn`
- [ ] FR-C008: 누락 Property 검사 — `BeamElement.PropertyId`에 해당하는 `BeamSection` 없음 → `Error`
- [ ] FR-C008: 누락 Material 검사 — `BeamSection.MaterialId`에 해당하는 `Material` 없음 → `Error`
- [ ] FR-C008: `Error` 진단이 1개 이상이면 BDF 출력 차단, exit code 2 반환
- [ ] FR-C008: 최종 BDF 파일 생성 (`BdfWriter` 사용)
- [ ] FR-C008: 최종 JSON 덤프 — `out/stages/08_FinalValidation.json`
- [ ] FR-C008: 진단 코드 — `FREE_END_NODE`, `ORPHAN_NODE`, `MISSING_PROPERTY`, `MISSING_MATERIAL`
- [ ] FR-C009: `cmb build-full --input <folder> --output <dir> [--stopat <stageName|index>] [--dump-json] [--dump-bdf-per-stage]` 실행 가능
- [ ] FR-C009: `--stopat` — 지정 스테이지 완료 후 중단 (이름 또는 0-based 인덱스)
- [ ] FR-C009: `--dump-json` — 각 스테이지 후 `out/stages/{idx:D2}_{stageName}.json` 생성 (기본 off)
- [ ] FR-C009: `--dump-bdf-per-stage` — 각 스테이지 후 BDF 스냅샷 생성 (기본 off)
- [ ] FR-C009: `Error` 진단 존재 시 exit code 2, 경고만 있으면 exit code 0
- [ ] FR-C009: 전체 파이프라인 `PipelineReport` 요약을 stdout으로 출력
- [ ] Phase C-4 통합 검증: `dotnet test` 전체 통과, `hitess_mini` fixture 전체 파이프라인 1초 이내 완료

---

> **체크포인트 C**: `out/stages` JSON + `final.bdf`를 시각화 및 Nastran 문법 검증 후 완료

---

## 의존 관계 그래프

```
FR-A001 (솔루션 스켈레톤)
    |
    +---> FR-A002 (Point3/Vector3/Segment3) ---+
    |                                           |
    +---> FR-A003 (도메인 모델 엔티티) ----------+--> FR-A004 (FeModel + JSON)
                                                              |
                                                         FR-A005 (CSV 리더 3종)
                                                              |
                                                         FR-A006 (cmb parse CLI)
                                                              |
                                                   [체크포인트 A]
                                                              |
                                         +--------------------+--------------------+
                                         |                                         |
                                    FR-B001 (FeModelBuilder)              FR-B002 (BdfField / BdfWriter)
                                         |                                         |
                                         +-------------------+---------------------+
                                                             |
                                                        FR-B003 (cmb build-raw CLI)
                                                             |
                                                   [체크포인트 B]
                                                             |
                                                        FR-C000 (파이프라인 인프라)
                                                             |
                                                        FR-C001 (SanityPreprocessStage)
                                                             |
                                                        FR-C002 (MeshingStage)
                                                             |
                                                        FR-C003 (NodeEquivalenceStage)
                                                             |
                                                        FR-C004 (IntersectionStage)
                                                             |
                                                        FR-C005 (WeldNodeStage)
                                                             |
                                                        FR-C006 (GroupConnectStage)
                                                             |
                                                        FR-C007 (UboltRbeStage)
                                                             |
                                                        FR-C008 (FinalValidation + Export)
                                                             |
                                                        FR-C009 (cmb build-full CLI)
                                                             |
                                                   [체크포인트 C]
```

---

## 이번 범위에서 제외

| 항목 | 제외 이유 | 다음 단계 |
|------|-----------|-----------|
| GUI 또는 웹 인터페이스 | PRD 1.2 명시 — 이번 범위 밖 | 별도 PRD 작성 필요 |
| Nastran 솔버 실행 및 결과 후처리 | PRD 1.2 명시 — 이번 범위 밖 | 별도 PRD 작성 필요 |
| AM 이외의 3D CAD 포맷 지원 | PRD 1.2 명시 — 이번 범위 밖 | 별도 PRD 작성 필요 |
| 비선형 해석 요소 (CBUSH, CGAP 등) | PRD 1.2 명시 — 이번 범위 밖 | 별도 PRD 작성 필요 |
| 레거시 `HiTessModelBuilder_26_01` 코드 수정 | PRD 1.2 명시 — 읽기 전용 참조만 허용 | 해당 없음 (참조 전용) |
| `ExtraData: Dictionary<string, string>` 패턴 | PRD 10 비채택 — 문자열 키 오타 위험, 타입 안전성 없음 | `EntityCategory` enum + `NodeTags [Flags]`로 대체 |
| `Console.WriteLine` 직접 호출 | PRD NFR-005 금지 — 테스트 불가, 형식 미제어 | `ILogger<T>`로 대체 |
| 하드코딩 tolerance 값 | PRD 10 비채택 — 재현 불가, 튜닝 불가 | `RunOptions.Tolerances` 레코드로 대체 |

---

## 참고: 요구사항 전체 목록

| ID | 요구사항 | 배치 단계 | 대상 모듈 |
|----|----------|-----------|-----------|
| FR-A001 | 솔루션 스켈레톤 생성 | Phase A-1 | `.sln`, `Directory.Build.props`, 4+4 프로젝트 |
| FR-A002 | 기하 Primitive 구현 | Phase A-1 | `Cmb.Core/Geometry` (`Point3`, `Vector3`, `Segment3`) |
| FR-A003 | 도메인 모델 뼈대 구현 | Phase A-2 | `Cmb.Core/Model` (Node, BeamElement, RigidElement, PointMass, BeamSection, Material, IdAllocator) |
| FR-A004 | FeModel 및 JSON 직렬화 구현 | Phase A-2 | `Cmb.Core/Model/Context/FeModel`, `Cmb.Core/Serialization/FeModelJson` |
| FR-A005 | HiTess CSV 리더 구현 | Phase A-3 | `Cmb.Io/Csv` (HiTessStructureCsvReader, HiTessPipeCsvReader, HiTessEquipCsvReader, CsvDesignLoader) |
| FR-A006 | `cmb parse` CLI 서브커맨드 구현 | Phase A-4 | `Cmb.Cli/Commands/ParseCommand` |
| FR-B001 | FeModelBuilder 구현 | Phase B-1 | `Cmb.Pipeline/Build/FeModelBuilder` |
| FR-B002 | Nastran BDF Writer 구현 | Phase B-1 | `Cmb.Io/Nastran` (`BdfField`, `BdfWriter`) |
| FR-B003 | `cmb build-raw` CLI 서브커맨드 구현 | Phase B-2 | `Cmb.Cli/Commands/BuildRawCommand` |
| FR-C000 | 파이프라인 인프라 구현 | Phase C-1 | `Cmb.Pipeline/Core`, `Cmb.Pipeline/Spatial` |
| FR-C001 | SanityPreprocessStage | Phase C-2 | `Cmb.Pipeline/Stages/SanityPreprocessStage` |
| FR-C002 | MeshingStage | Phase C-2 | `Cmb.Pipeline/Stages/MeshingStage` |
| FR-C003 | NodeEquivalenceStage | Phase C-2 | `Cmb.Pipeline/Stages/NodeEquivalenceStage` |
| FR-C004 | IntersectionStage | Phase C-3 | `Cmb.Pipeline/Stages/IntersectionStage` |
| FR-C005 | WeldNodeStage | Phase C-3 | `Cmb.Pipeline/Stages/WeldNodeStage` |
| FR-C006 | GroupConnectStage | Phase C-3 | `Cmb.Pipeline/Stages/GroupConnectStage` |
| FR-C007 | UboltRbeStage | Phase C-3 | `Cmb.Pipeline/Stages/UboltRbeStage` |
| FR-C008 | FinalValidationStage + ExportStage | Phase C-4 | `Cmb.Pipeline/Stages/FinalValidationStage`, `Cmb.Pipeline/Stages/ExportStage` |
| FR-C009 | `cmb build-full` CLI 서브커맨드 구현 | Phase C-4 | `Cmb.Cli/Commands/BuildFullCommand` |

---

## 참고: 비기능 요구사항 체크리스트

| ID | 항목 | 기준 |
|----|------|------|
| NFR-001 | 빌드 품질 | `dotnet build` 경고 0, 오류 0. `Nullable=enable`, `TreatWarningsAsErrors=true` |
| NFR-002 | 테스트 커버리지 | `Cmb.Core` 80% 이상, `Cmb.Pipeline` Stage당 최소 2개, 전체 70% 이상 |
| NFR-003 | 성능 | `hitess_mini` 전체 파이프라인 1초 이내. `NodeEquivalenceStage` O(N log N). `IntersectionStage` 공간 해시 필수 |
| NFR-004 | 호환성 | .NET 8.0, C# 12, MSC Nastran 2019 이상 BDF 호환 |
| NFR-005 | 로깅 | `Console.WriteLine` 금지. `ILogger<T>` 사용. Stage 시작/완료 Information 로그. Diagnostic 대응 로그 수준 자동 매핑 |
| NFR-006 | JSON 스키마 버전 관리 | `meta.schemaVersion` 필수. 변경 시 `docs/json-schema.md` 업데이트 및 버전 번호 증가 |
