using Xceed.Words.NET;
var doc = DocX.Load("./docx/790-qd-dhcntt_28-9-22_quy_che_dao_tao.docx");
var allText = string.Join("\n", doc.Paragraphs.Select(p => p.Text));
var lines = allText.Split('\n');
for(int i=0; i<lines.Length; i++) {
    if(lines[i].Contains("Điều 16") || lines[i].Contains("xử lý học vụ") || lines[i].Contains("Cảnh báo học vụ") || lines[i].Contains("Đình chỉ học tập")) {
        Console.WriteLine($"Line {i}: {lines[i]}");
    }
}
