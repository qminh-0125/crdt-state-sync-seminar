# StateSync: CRDT cho đồng bộ giỏ hàng chia sẻ

**Seminar — Trần Quang Minh — MSSV 23110187**

Tên đề tài: Mô hình hóa và cài đặt thuật toán đồng bộ CRDT cho ứng dụng web chia sẻ trạng thái thời gian thực.

## 1. Mục tiêu và phạm vi

So sánh LWW-Element-Set có tombstone với baseline tự cài đặt `NaiveLWWSet` không lưu tombstone. Baseline cố ý giữ hạn chế này để đo lỗi mất thao tác xóa/hồi sinh phần tử. LWW-Element-Set cũng là một CRDT; kết quả không có nghĩa mọi thuật toán LWW đều sai.

- **Core:** LWW-Element-Set (max timestamp, remove thắng khi bằng timestamp), OR-Set (add-wins), vector clock, ma trận tiến độ nhân quả và khóa theo thứ tự chung. LWW hỗ trợ cập nhật toàn bộ payload của `ShoppingCartItem` theo `ProductId`.
- **Benchmark:** nhiều replica độc lập trong một tiến trình, mạng thời gian ảo có jitter/duplicate/partition; 17 cấu hình, mỗi cấu hình 5 trial. Các lịch thao tác mô phỏng cạnh tranh được thực thi tuần tự; đây chưa phải đo tải HTTP đa luồng hay 50 trình duyệt thật.
- **Web:** demo SignalR tập trung; server giữ cả hai tập và phát snapshot cho browser. Browser không giữ replica CRDT/offline outbox độc lập. Nút GC thu gọn trạng thái server; GC dùng ma trận vector clock giữa replica được kiểm tra trong Core/Benchmark.

Kết quả kiểm thử là bằng chứng thực nghiệm trong các điều kiện này, không phải chứng minh chương trình không thể có lỗi.

## 2. Cấu trúc

```text
src/StateSync.Core/          Thuật toán, payload, timestamp, vector clock, GC
src/StateSync.NetworkSim/    Mô phỏng mạng thời gian ảo và bất đồng bộ
src/StateSync.Server/        Hub, trạng thái tập trung, dashboard
src/StateSync.Tests/         Test thuộc tính và test hồi quy
src/StateSync.Benchmark/     Sinh workload, oracle độc lập, đo và xuất kết quả
scripts/verify-web.mjs       Kiểm tra HTTP/SignalR thật và logic giao diện
results.csv                 17 dòng kết quả của lần chạy đã ghi nhận
metrics_summary.json        Cùng kết quả, giữ độ chính xác số đầy đủ
error_analysis_cases.json   Hai ca lỗi baseline được truy vết
```

## 3. Chạy và kiểm tra

Yêu cầu .NET SDK 10.0+. Khởi chạy từ thư mục gốc repository:

```sh
dotnet build src/StateSync.slnx -c Release
dotnet test src/StateSync.Tests/StateSync.Tests.csproj -c Release
dotnet run --project src/StateSync.Benchmark/StateSync.Benchmark.csproj -c Release
```

Benchmark ghi đè ba tệp kết quả ở **thư mục làm việc hiện tại**. Seed: Exp1 `42..46`; Exp2A `100 + clients*10 + trial`; Exp2B `300..304`; Exp3 `2026..2030`; Exp4 `77..81`. Throughput/thời gian có thể đổi theo máy, JIT, GC và tải nền; các trường trạng thái phải tái lập với cùng workload.

Chạy demo:

```sh
dotnet run --project src/StateSync.Server/StateSync.Server.csproj --no-launch-profile --urls http://localhost:5209
```

Mở `http://localhost:5209` ở hai tab. Giao diện cần truy cập CDN để tải SignalR JavaScript. Trong terminal khác, có thể chạy kiểm tra với Node.js 22+ (không cần npm install):

```sh
node scripts/verify-web.mjs http://localhost:5209
```

Script kiểm tra sẽ thêm/xóa dữ liệu demo và chạy GC, nên dùng trên phiên demo dành cho test. Nó kiểm tra giao thức thật và logic DOM trong môi trường giả lập, chưa thay thế việc quan sát bố cục/tương tác trên mọi trình duyệt.

## 4. Hợp đồng timestamp và GC

Mỗi thao tác mới của cùng `ClientId` phải có tick mới tăng dần. Retry phải giữ nguyên `ClientId`, tick, loại thao tác và payload, kể cả sau reconnect. Frontend chờ snapshot, lấy max với `serverLogicalTick`, tăng 10 rồi gửi; server **không đổi timestamp của retry**. Khi cùng timestamp nhưng payload khác nhau, Core chọn payload theo chuỗi chuẩn hóa đầy đủ để merge xác định; đó không phải hai thao tác mới hợp lệ của cùng client.

`PruneTombstones` đòi hỏi một prefix ổn định toàn cục: mọi replica cố định đã nhận các thao tác tới ngưỡng GC, và không có thao tác mới hợp lệ sinh dưới ngưỡng đó. Ma trận trong benchmark cung cấp ngưỡng bảo thủ `min M[observer][origin]`. Một lần phát broadcast không chứng minh browser đã ACK. Demo tập trung chỉ có một chủ trạng thái và áp dụng cutoff để từ chối thao tác cũ; không hỗ trợ chỉnh sửa offline trước cutoff rồi nộp lại như thao tác mới.

Server chỉ giữ dữ liệu trong RAM. Khởi động lại tạo phiên demo trống; không có lưu bền, xác thực người dùng hay giao thức thay đổi membership phân tán. Các phần này nằm ngoài phạm vi baseline hiện tại.

## 5. Định nghĩa metric

1. **ConvergenceRate:** số cặp replica có tập phần tử active bằng nhau / tổng số cặp × 100. Hội tụ với nhau vẫn có thể cùng sai so với oracle.
2. **StateDiscrepancy:** với mỗi replica, `|S △ Oracle| / |S ∪ Oracle| × 100`, quy ước 0 khi cả hai rỗng; lấy trung bình trên replica rồi mean/sample std qua 5 trial. Bao gồm phần tử thiếu và phần tử hồi sinh sai.
3. **OracleAgreement:** phần trăm replica có tập active khớp hoàn toàn oracle. Oracle được tính độc lập từ max Add/Remove trong nhật ký thao tác. Workload benchmark dùng khóa chuỗi; test payload giỏ hàng kiểm tra riêng Name/Price/Quantity.
4. **MeanTrialP95BatchSyncElapsed(ms):** mỗi trial lấy P95 nearest-rank của elapsed wall-clock xử lý các đợt sync, rồi lấy mean qua 5 trial. Đo bằng `Stopwatch`; không phải riêng CPU time và không phải P95 hội tụ đầu-cuối qua mạng. Broadcast đo cả lọc/sắp hàng đợi và áp dụng bản tin; stress/GC đo merge và bảo trì định kỳ; Exp3 thêm một mẫu gather/broadcast phục hồi. Chu kỳ là 250 thao tác, cộng flush cuối nếu còn gói tin. Chỉ so sánh thời gian trong cùng thí nghiệm/giao thức.
5. **Throughput:** số thao tác workload / elapsed toàn vòng chạy, không phải số RPC/s hoặc số bản tin/s. Tính cả chi phí mô phỏng; bước tạo workload/clock khác giữa thí nghiệm. Exp1 truy vết lỗi bằng replay ngoài cửa sổ đo để tránh tính thêm logging riêng cho baseline. Chưa kiểm soát warm-up hay cô lập tải nền, nên số hiệu năng mang tính thăm dò.
6. **MetadataOverhead và EstimatedRAM:** ước lượng theo mô hình UTF-8 khóa/client, tick 8 B, payload chuỗi cộng 48 B hoặc thuộc tính giỏ hàng, dictionary 32 B/entry. Không phải profiler/working set thực; không bao gồm vector clock, khóa, cutoff, simulator, runtime và toàn bộ object overhead. Khi payload=0 còn metadata, UI ghi N/A; giá trị số 100 trong API Core là quy ước sentinel, không áp dụng phép chia metadata/payload lúc mẫu số bằng 0. Khi cả hai bằng 0 trả 0.
7. **InvariantPass:** phần trăm đạt trong 4 kiểm tra sau trial: idempotence, commutativity, associativity và khớp oracle. CRDT kiểm tra cả trạng thái semilattice cho 3 luật merge; baseline kiểm tra luật trên tập active. Đây là điểm kiểm tra thực nghiệm trên mẫu trạng thái cuối, không phải tỷ lệ bảo toàn mọi bất biến nghiệp vụ ở mọi bước. Bộ test ngẫu nhiên kiểm tra các luật riêng trên trạng thái chưa đồng bộ.

Exp3 dùng cùng partition và cùng bước gather vào node 0 rồi broadcast tới các node còn lại cho cả hai thuật toán. Sau heal, bước merge cuối dùng lời gọi in-process; không đo độ trễ phục hồi qua đường truyền thật. Mức tranh chấp Exp2B được định nghĩa bởi pool 500/100/20, không gán thành tỷ lệ conflict 10/50/90% chưa đo.

## 6. Kết quả ghi nhận ngày 16/10/2026

Môi trường: Windows, Intel Core i5-13500HX (14 core/20 logical processors), RAM hệ thống khoảng 15.7 GiB, SDK 10.0.204 / runtime 10.0.8. Release build 0 lỗi/0 cảnh báo; **33/33 xUnit tests pass**. Kiểm tra HTTP/SignalR và logic frontend pass.

Bảng được cập nhật từ lần chạy thật cùng `metrics_summary.json`; std là độ lệch chuẩn mẫu qua 5 trial.

| Kịch bản | Clients | Ops | Hội tụ % | Sai lệch mean ± std % | Mean trial P95 elapsed ms | Throughput mean ± std ops/s | Tombstone | Meta % | RAM KB | Invariant % | Oracle % |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Exp1_CRDT_LWW_Element_Set | 10 | 10000 | 100.0 | 0.00 ± 0.00 | 3.529 | 104175 ± 54418 | 499 | 115.8 | 70.1 | 100.0 | 100.0 |
| Exp1_Baseline_Naive_LWW | 10 | 10000 | 0.0 | 2.08 ± 0.80 | 2.190 | 129607 ± 64666 | 0 | 28.6 | 34.2 | 75.0 | 2.0 |
| Exp2A_Stress_2Clients_25kOps | 2 | 25000 | 100.0 | 0.00 ± 0.00 | 0.228 | 708208 ± 254252 | 83 | 34.8 | 38.0 | 100.0 | 100.0 |
| Exp2A_Stress_5Clients_25kOps | 5 | 25000 | 100.0 | 0.00 ± 0.00 | 0.665 | 393344 ± 115137 | 244 | 60.4 | 50.6 | 100.0 | 100.0 |
| Exp2A_Stress_10Clients_25kOps | 10 | 25000 | 100.0 | 0.00 ± 0.00 | 0.574 | 491539 ± 25586 | 397 | 79.8 | 59.7 | 100.0 | 100.0 |
| Exp2A_Stress_20Clients_25kOps | 20 | 25000 | 100.0 | 0.00 ± 0.00 | 1.285 | 217707 ± 45999 | 483 | 88.8 | 65.0 | 100.0 | 100.0 |
| Exp2A_Stress_50Clients_50kOps | 50 | 50000 | 100.0 | 0.00 ± 0.00 | 2.943 | 89450 ± 11903 | 500 | 91.9 | 66.2 | 100.0 | 100.0 |
| Exp2B_LowContention_Pool500_CRDT | 10 | 10000 | 100.0 | 0.00 ± 0.00 | 1.112 | 146080 ± 5731 | 500 | 108.7 | 67.7 | 100.0 | 100.0 |
| Exp2B_LowContention_Pool500_Baseline | 10 | 10000 | 0.0 | 2.95 ± 0.84 | 1.027 | 159618 ± 16143 | 0 | 18.3 | 30.9 | 75.0 | 0.0 |
| Exp2B_MedContention_Pool100_CRDT | 10 | 10000 | 100.0 | 0.00 ± 0.00 | 1.048 | 150129 ± 3680 | 100 | 115.2 | 13.4 | 100.0 | 100.0 |
| Exp2B_MedContention_Pool100_Baseline | 10 | 10000 | 0.0 | 9.42 ± 2.52 | 0.985 | 163098 ± 6437 | 0 | 18.3 | 6.3 | 75.0 | 0.0 |
| Exp2B_HighContention_Pool20_CRDT | 10 | 10000 | 100.0 | 0.00 ± 0.00 | 0.957 | 160163 ± 7914 | 20 | 146.0 | 2.6 | 100.0 | 100.0 |
| Exp2B_HighContention_Pool20_Baseline | 10 | 10000 | 1.3 | 29.76 ± 16.02 | 0.887 | 174522 ± 9822 | 0 | 18.3 | 1.4 | 75.0 | 4.0 |
| Exp3_Partition_CRDT | 4 | 10000 | 100.0 | 0.00 ± 0.00 | 0.121 | 687998 ± 41314 | 200 | 122.8 | 31.1 | 100.0 | 100.0 |
| Exp3_Partition_Baseline | 4 | 10000 | 100.0 | 27.87 ± 3.25 | 0.106 | 730867 ± 92471 | 0 | 21.2 | 19.6 | 75.0 | 0.0 |
| Exp4_Ablation_GC_OFF_50kOps | 4 | 50000 | 100.0 | 0.00 ± 0.00 | 1.946 | 160205 ± 5609 | 4940 | 156.7 | 748.0 | 100.0 | 100.0 |
| Exp4_Ablation_GC_ON_50kOps | 4 | 50000 | 100.0 | 0.00 ± 0.00 | 1.535 | 198924 ± 11159 | 158 | 29.5 | 314.1 | 100.0 | 100.0 |

CRDT khớp oracle trong mọi cấu hình đã chạy. Baseline cố ý bộc lộ lỗi: Exp3 vẫn hội tụ 100% nhưng sai lệch khoảng 27.87%; đây là kết quả đối chứng, không phải test bị hỏng. GC ON giảm overhead ước lượng xuống khoảng 29.5% trong workload Exp4 này; không suy rộng thành cam kết cho mọi workload.

## 7. Paper tham khảo

1. Marc Shapiro, Nuno Preguiça, Carlos Baquero, Marek Zawirski (2011), *Conflict-Free Replicated Data Types*, SSS / INRIA RR-7687.
2. Cùng nhóm tác giả (2011), *A comprehensive study of Convergent and Commutative Replicated Data Types*, INRIA RR-7506.

Hai paper là cơ sở thuật toán. Baseline và số liệu thực nghiệm do dự án tự cài đặt/tự chạy, không phải số hiệu năng chính thức lấy từ paper.
