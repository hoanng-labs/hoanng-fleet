# Changelog

## v1.0.0 — 2026-11-03

Phiên bản đầu tiên.

- Danh sách máy: thêm / sửa / xoá / ghi chú / tìm kiếm, trạng thái màu, khung Thông tin chính.
- Khoá SSH ed25519 riêng cho từng máy; mật khẩu chỉ dùng một lần, không lưu.
- Kết nối SSH và mở Claude Code thành tab Windows Terminal đặt theo tên máy; mở Claude ở thư mục bất kỳ.
- Cài máy ảo Ubuntu làm máy agent (Claude Code chạy root, script ghim `ubuntu-agent-vm@fe5b44c`), cập nhật Claude Code.
- Cài lại khoá (máy dựng lại / khoá bị gỡ), nhập kết nối có sẵn, sao lưu / khôi phục zip, xoá an toàn vào thùng rác.
- Quản lý tệp hai nửa Windows ↔ SSH với cây thư mục, tải lên / tải về theo mục đã chọn, kéo thả, hỏi khi trùng tên.
- Đồng bộ sao lưu chỉ tải tệp mới / đã đổi, chế độ gương, tự chạy định kỳ qua Task Scheduler.
