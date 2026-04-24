# 프로젝트 진행 스냅샷 — Phase A-2 완료 시점

**스냅샷 날짜:** 2026-04-24  
**현재 진행:** A-1 ✅ A-2 ✅ A-3 🔄 (진행 중)

---

## 전체 태스크 현황

| Phase | 태스크명 | 상태 | 완료 시각 |
|-------|----------|------|-----------|
| A-1 | 솔루션 스켈레톤 + 기하 Primitive | ✅ 완료 | 2026-04-24 09:00 |
| A-2 | 도메인 모델 + JSON 직렬화 | ✅ 완료 | 2026-04-24 09:05 |
| A-3 | HiTess CSV 리더 구현 | 🔄 진행 중 | — |
| A-4 | cmb parse CLI 서브커맨드 | ⏳ 대기 | — |
| B-1 | FeModelBuilder + Nastran BDF Writer | ⏳ 대기 | — |
| B-2 | cmb build-raw CLI 서브커맨드 | ⏳ 대기 | — |
| C-1 | 파이프라인 인프라 + 공간 자료구조 | ⏳ 대기 | — |
| C-2 | 전처리 3단계 Stage 구현 | ⏳ 대기 | — |
| C-3 | 교차·연결·RBE2 Stage 구현 | ⏳ 대기 | — |
| C-4 | FinalValidation + cmb build-full CLI | ⏳ 대기 | — |

**전체 진척:** 2 / 10 완료 (20%)

---

## 테스트 현황 (스냅샷 시점)

| 프로젝트 | 통과 | 실패 | 비고 |
|----------|------|------|------|
| Cmb.Core.Tests | 59 | 0 | 기하 + 도메인 모델 + JSON |
| Cmb.Io.Tests | 27 | 0 | CSV 리더 (A-3 부분 완성) |
| Cmb.Pipeline.Tests | 1 | 0 | Placeholder |
| Cmb.Integration.Tests | 1 | 0 | Placeholder |
| **합계** | **88** | **0** | — |

---

## Phase A-1 완료 내용

**태스크 ID:** `3e94455d-d7a4-4326-9eba-ce8e428e524a`

### 구현 파일
| 파일 | 설명 |
|------|------|
| `Directory.Build.props` | Nullable=enable, TreatWarningsAsErrors=true, LangVersion=latest 전역 설정 |
| `src/Cmb.Core/Geometry/Point3.cs` | `readonly struct Point3 : IEquatable<Point3>` — X,Y,Z(double), DistanceTo, 연산자 |
| `src/Cmb.Core/Geometry/Vector3.cs` | `readonly struct Vector3` — Length, Normalize, Dot, Cross, 연산자 |
| `src/Cmb.Core/Geometry/Segment3.cs` | `readonly struct Segment3` — Start/End, Length, Midpoint, ClosestPointTo |
| `tests/Cmb.Core.Tests/Geometry/Point3Tests.cs` | Point3 단위 테스트 |
| `tests/Cmb.Core.Tests/Geometry/Vector3Tests.cs` | Vector3 단위 테스트 |
| `tests/Cmb.Core.Tests/Geometry/Segment3Tests.cs` | Segment3 단위 테스트 |

### 검증 결과
- 빌드: 경고 0, 오류 0
- 단위 테스트: 37개 통과 (요구 20개 이상 충족)
- 의존성 방향: `Cmb.Core → Cmb.Io → Cmb.Pipeline → Cmb.Cli` 단방향 준수
- `.gitignore`: out/, bin/, obj/ 추가

---

## Phase A-2 완료 내용

**태스크 ID:** `51ff7cbb-fa21-43de-ba80-b1e7174f9c4f`

### 구현 파일
| 파일 | 설명 |
|------|------|
| `src/Cmb.Core/Model/Enums.cs` | `[Flags] NodeTags`, `BeamSectionKind`, `EntityCategory` |
| `src/Cmb.Core/Model/Node.cs` | `sealed class Node` — Id, Point3 Position, NodeTags Tags |
| `src/Cmb.Core/Model/BeamElement.cs` | `sealed class BeamElement` — Id, StartNodeId, EndNodeId, PropertyId, EntityCategory, Vector3 Orientation |
| `src/Cmb.Core/Model/RigidElement.cs` | Id, IndependentNodeId, IReadOnlyList\<int\> DependentNodeIds, Remark |
| `src/Cmb.Core/Model/PointMass.cs` | Id, NodeId, double Mass |
| `src/Cmb.Core/Model/BeamSection.cs` | `record` — Id, BeamSectionKind Kind, double[] Dims, MaterialId |
| `src/Cmb.Core/Model/Material.cs` | `record` — Id, Name, E, Nu, Rho. 기본값 Steel(206000, 0.3, 7.85e-9) |
| `src/Cmb.Core/Model/IdAllocator.cs` | `int Next()` — 단조 증가 ID 발급 |
| `src/Cmb.Core/Model/Context/FeModel.cs` | 모든 엔티티 컨테이너, LengthUnit="mm" |
| `src/Cmb.Core/Model/Raw/RawDesignData.cs` | RawBeamRow, RawPipeRow, RawEquipRow DTO |
| `src/Cmb.Core/Serialization/FeModelJson.cs` | Source Generator `FeModelJsonContext`, ToJson()/FromJson() 확장 메서드 |
| `tests/Cmb.Core.Tests/Model/ModelEntitiesTests.cs` | 도메인 엔티티 단위 테스트 |
| `tests/Cmb.Core.Tests/Serialization/FeModelJsonTests.cs` | 왕복 직렬화 + NodeTags 비트 플래그 테스트 |

### 검증 결과
- 빌드: 경고 0, 오류 0
- 테스트: 59개 전체 통과
  - NodeTags 비트 OR/다중 플래그 직렬화 검증
  - FeModel 왕복(직렬화 → 역직렬화 → 동치) 검증
- JSON: camelCase 필드명, PRD 6.1 스키마와 1:1 매핑
- Source Generator: AOT 호환

---

## Phase A-3 진행 중 (현황)

**태스크 ID:** `624ce2c8-c4c5-41f3-8ff2-90d3bf05aa84`

### 이미 생성된 파일
| 파일 | 상태 |
|------|------|
| `src/Cmb.Io/Csv/CsvSchema.cs` | ✅ 생성됨 |
| `src/Cmb.Io/Csv/CsvParsing.cs` | ✅ 생성됨 |
| `src/Cmb.Io/Csv/HiTessStructureCsvReader.cs` | ✅ 생성됨 |
| `src/Cmb.Io/Csv/HiTessPipeCsvReader.cs` | ✅ 생성됨 |
| `src/Cmb.Io/Csv/HiTessEquipCsvReader.cs` | ✅ 생성됨 |
| `src/Cmb.Io/Csv/CsvDesignLoader.cs` | ✅ 생성됨 |
| `samples/hitess_mini/structure.csv` | ✅ 생성됨 |
| `samples/hitess_mini/pipe.csv` | ✅ 생성됨 |
| `samples/hitess_mini/equip.csv` | ✅ 생성됨 |
| `tests/Cmb.Io.Tests/Csv/HiTessStructureCsvReaderTests.cs` | ✅ 생성됨 |
| `tests/Cmb.Io.Tests/Csv/HiTessPipeCsvReaderTests.cs` | ✅ 생성됨 |
| `tests/Cmb.Io.Tests/Csv/HiTessEquipCsvReaderTests.cs` | ✅ 생성됨 |
| `tests/Cmb.Io.Tests/Csv/CsvDesignLoaderTests.cs` | ✅ 생성됨 |

### 현재 테스트 결과
- `Cmb.Io.Tests`: 27개 통과 / 0개 실패

### 완료 기준 (미검증)
- [ ] Verify.Xunit 스냅샷 테스트 통과 확인
- [ ] 헤더 스킵, 빈 행 무시 동작 확인
- [ ] Shrimp 태스크 complete 처리

---

## 솔루션 구조 현황

```
ClaudeModelBuilder.sln
Directory.Build.props
src/
  Cmb.Core/
    Geometry/       Point3, Vector3, Segment3
    Model/          Enums, Node, BeamElement, RigidElement, PointMass, BeamSection, Material, IdAllocator
    Model/Context/  FeModel
    Model/Raw/      RawDesignData
    Serialization/  FeModelJson (Source Generator)
  Cmb.Io/
    Csv/            CsvSchema, CsvParsing, 3종 리더, CsvDesignLoader  ← A-3 진행 중
  Cmb.Pipeline/     (미구현)
  Cmb.Cli/          Program.cs (빈 진입점)
tests/
  Cmb.Core.Tests/   59개 통과
  Cmb.Io.Tests/     27개 통과
  Cmb.Pipeline.Tests/ 1개 (placeholder)
  Cmb.Integration.Tests/ 1개 (placeholder)
samples/hitess_mini/  structure.csv, pipe.csv, equip.csv
```

---

## 다음 단계

**A-3 완료 후 → A-4: cmb parse CLI 서브커맨드**
- System.CommandLine 기반 `cmb parse --input <folder> --output <dir>`
- `out/raw/*.raw.json` 생성
- 통합 테스트 (exit code 1 포함)
- 의존성: `Cmb.Cli → Cmb.Io` 연결
