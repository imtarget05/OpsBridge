# ADR 026: FR-C07 SKU Forecast — Phạm vi phát hành

## 1. Status

**Accepted** (2026-10-26). Quyết định bởi owner uỷ quyền (phiên hoàn thiện 100% release).

## 2. Context

`FR-C07` — "Dự báo nhu cầu theo SKU" nằm trong functional-requirements.md, thuộc nhóm sau MVP. ADR/PRD chưa chốt rõ forecast có thuộc mẫu số MVP/toàn phạm vi phát hành hay không. Ma trận nghiệm thu Phase 0 để dòng này ở `scope_pending`, khiến tỷ lệ hoàn thành không thể tính chính thức.

## 3. Decision

1. **FR-C07 KHÔNG thuộc MVP phát hành lần này.** Lý do:
   - Cần bộ dữ liệu đánh giá chất lượng (forecast eval set) chưa có — không thể chứng minh độ chính xác dự báo.
   - Cần provider/khóa ở server đã hoàn tất (FR-C05) nhưng chưa qua UAT cloud với dữ liệu thật.
   - PRD xếp nhu cầu vận hành thực tế (đối soát, phê duyệt, stuck-order) ở Must; forecast là Should/Later.
2. **FR-C07 giữ ở mẫu số toàn phạm vi (full)** — vẫn là cam kết dài hạn, không loại khỏi sản phẩm.
3. Điều kiện đưa vào phát hành sau: (a) eval dataset ≥ 100 case và quality gate ≥ ngưỡng ghi trong docs/requirements/acceptance-criteria.md; (b) provider fallback đã UAT; (c) có owner chấp nhận.

## 4. Consequences

- Ma trận nghiệm thu: dòng FR-C07 đổi `scope_pending` → `scope_resolved_full_deferred`, **không tính vào MVP %**; tính vào full-scope % với score hiện tại.
- Không làm chặn tuyên bố "MVP 100%" sau khi các dòng MVP đạt chứng cứ.
- Backlog: tạo issue "FR-C07: forecast eval dataset + quality gate" để theo dõi.
