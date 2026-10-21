import { useState, useRef, useEffect } from 'react';
import {
  Sparkles,
  X,
  Send,
  Bot,
  User,
  Settings,
  ShieldAlert,
} from 'lucide-react';
import { Button } from '../ui/Button';
import { Input } from '../ui/Input';

interface Message {
  id: string;
  sender: 'ai' | 'user';
  text: string;
  timestamp: string;
  isFallback?: boolean;
}

const DEFAULT_API_KEY = 'AIzaSyCd3WrR0YwDiwcwDki-KieIOoYUTcflCKA';

const INITIAL_MESSAGES: Message[] = [
  {
    id: 'welcome-1',
    sender: 'ai',
    text: 'Xin chào! Tôi là Trợ lý AI Đối Soát (OpsCopilot). Tôi có thể giúp bạn giải thích quy trình 3-Way Match, phân tích nguyên nhân hóa đơn bị lệch số lượng/đơn giá, và hướng dẫn các bước thao tác trên hệ thống OpsBridge!',
    timestamp: '09:00',
  },
];

export function OpsCopilotWidget() {
  const [isOpen, setIsOpen] = useState(false);
  const [apiKey, setApiKey] = useState(() => {
    return localStorage.getItem('opsbridge_gemini_key') || DEFAULT_API_KEY;
  });
  const [showSettings, setShowSettings] = useState(false);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [messages, setMessages] = useState<Message[]>(INITIAL_MESSAGES);

  const messagesEndRef = useRef<HTMLDivElement>(null);

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    if (isOpen) {
      scrollToBottom();
    }
  }, [messages, isOpen]);

  const handleSaveKey = (newKey: string) => {
    setApiKey(newKey);
    localStorage.setItem('opsbridge_gemini_key', newKey);
    setShowSettings(false);
  };

  // Local expert heuristic fallback when Gemini API key is blocked on Google Cloud Console
  const getHeuristicResponse = (query: string): string => {
    const q = query.toLowerCase();

    if (q.includes('quy trình') || q.includes('3 bên') || q.includes('3-way') || q.includes('là gì')) {
      return `📌 **Quy trình Đối soát 3 Bên (Three-Way Matching) gồm 4 bước:**
1. **Đơn mua hàng (PO):** Bộ phận mua hàng lập cam kết mua hàng với NCC (quy định rõ Số lượng đặt & Đơn giá thỏa thuận).
2. **Phiếu nhập kho (GR):** Thủ kho kiểm đếm thực tế khi xe giao hàng tới kho, ghi nhận số lượng thực tế nhận được.
3. **Hóa đơn NCC (INV):** NCC gửi hóa đơn tài chính yêu cầu thanh toán.
4. **Đối soát (3-Way Matching):** Hệ thống tự động so khớp chéo:
   - *Số lượng trên Hóa đơn* phải bằng *Số lượng thực nhận tại Kho (GR)*.
   - *Đơn giá trên Hóa đơn* phải bằng *Đơn giá cam kết trong Đơn mua (PO)*.
Nếu khớp 100% hoặc trong biên độ dung sai (Tolerance), hóa đơn sẽ chuyển sang trạng thái **Matched** để phòng Tài chính xuất tiền!`;
    }

    if (q.includes('8803') || q.includes('sai lệch') || q.includes('chênh lệch') || q.includes('lệch')) {
      return `🔍 **Phân tích Hóa đơn INV-2026-8803:**
- **Nhà cung cấp:** Công Ty TNHH Trân Châu & Topping Đài Loan.
- **Mã sản phẩm:** Trân Châu Đen Hoàng Gia 20kg (SKU-BOBA-BLACK-20K).
- **Thực tế:** 
  + Hóa đơn NCC yêu cầu thanh toán: **100 bao**.
  + Thực tế thủ kho kiểm đếm nhận được: **80 bao** (thiếu 20 bao do lỗi vận chuyển).
- **Sai lệch (Discrepancy):** Chênh lệch **20 bao**, tương đương giá trị vênh **13.200.000 VNĐ**.
- **Khuyến nghị xử lý:**
  👉 Nhân viên kế toán nên chọn hành động **Rejected (Từ chối)** hoặc **Adjusted (Yêu cầu NCC phát hành lại hóa đơn cho 80 bao thực nhận)** thay vì thanh toán toàn bộ!`;
    }

    if (q.includes('dung sai') || q.includes('tolerance')) {
      return `⚙️ **Chính sách Biên độ Dung sai (Tolerance Policy Engine):**
Hệ thống cho phép cấu hình 3 chỉ số linh hoạt:
1. **Price Tolerance (%):** Biên độ lệch đơn giá cho phép (mặc định 1.0%). Phù hợp cho trường hợp thuế phí làm tròn vài nghìn đồng.
2. **Qty Tolerance (%):** Biên độ lệch số lượng theo tỷ lệ %.
3. **Qty Absolute Tolerance:** Số lượng lệch tuyệt đối (ví dụ cho phép hao hụt 1-2 đơn vị bao bì với hàng rời/nông sản).
Nếu chênh lệch nằm trong biên độ này, hệ thống sẽ tự động duyệt với trạng thái **ApprovedWithTolerance**.`;
    }

    if (q.includes('bước') || q.includes('người mới') || q.includes('hướng dẫn') || q.includes('dùng')) {
      return `👋 **Hướng dẫn sử dụng nhanh cho người mới:**
- **Bước 1:** Vào mục **Đơn mua hàng (PO)** ở menu trái để kiểm tra danh sách đơn đặt hàng hiện có.
- **Bước 2:** Vào mục **Phiếu nhập kho (GR)** để xem hàng đã về kho chưa, thủ kho đã xác nhận bao nhiêu kiện.
- **Bước 3:** Vào mục **Hóa đơn NCC (INV)** để kiểm tra hóa đơn cần đối soát hoặc nhập mới hóa đơn.
- **Bước 4:** Vào mục **Đối soát 3 bên (3-Way Matching)**, chọn hóa đơn và nhấn nút xanh **"Thực Thi Đối Soát 3 Bên (Execute 3-Way Match)"**. Hệ thống sẽ tính toán và hiển thị kết quả ngay lập tức!`;
    }

    return `Tôi hiểu bạn đang hỏi về: "${query}".
Trong hệ thống OpsBridge, mọi giao dịch mua hàng đều được bảo vệ nghiêm ngặt qua quy tắc Đối soát 3 bên.
- Bạn có thể nhấn vào nút **"Execute 3-Way Match"** trong trang Đối soát để chạy kiểm tra chéo tự động.
- Nếu bạn có bất kỳ câu hỏi nào về số liệu hóa đơn cụ thể, cách tạo đơn PO hay xử lý chênh lệch, hãy gõ câu hỏi cho tôi nhé!`;
  };

  const handleSend = async (textToSend?: string) => {
    const question = (textToSend || input).trim();
    if (!question || loading) return;

    const userMsg: Message = {
      id: 'usr-' + Date.now(),
      sender: 'user',
      text: question,
      timestamp: new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }),
    };

    setMessages((prev) => [...prev, userMsg]);
    setInput('');
    setLoading(true);

    try {
      // Attempt live Gemini 2.5 call
      const prompt = `Bạn là Trợ lý AI Chuyên gia Vận hành Kho & Kế toán Tài chính của hệ thống OpsBridge (Three-Way Matching Reliability Platform).
Nhiệm vụ của bạn là giải thích quy trình đối soát 3 bên (Đơn mua PO, Phiếu nhập kho GR, Hóa đơn NCC INV), giải thích các lỗi sai lệch số lượng/đơn giá (QuantityMismatch, PriceMismatch), hướng dẫn người dùng sử dụng trang web một cách thân thiện, súc tích và dễ hiểu bằng tiếng Việt.
Câu hỏi của người dùng: "${question}"`;

      const response = await fetch(
        `https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=${apiKey}`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            contents: [{ parts: [{ text: prompt }] }],
          }),
        }
      );

      if (!response.ok) {
        throw new Error(`Gemini API HTTP ${response.status}`);
      }

      const data = await response.json();
      const aiReply =
        data.candidates?.[0]?.content?.parts?.[0]?.text ||
        getHeuristicResponse(question);

      setMessages((prev) => [
        ...prev,
        {
          id: 'ai-' + Date.now(),
          sender: 'ai',
          text: aiReply,
          timestamp: new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }),
        },
      ]);
    } catch (err: any) {
      console.warn('Gemini live call fallback to local heuristics:', err.message);
      // Fallback to rich internal expert engine
      const fallbackReply = getHeuristicResponse(question);
      setMessages((prev) => [
        ...prev,
        {
          id: 'ai-' + Date.now(),
          sender: 'ai',
          text: fallbackReply,
          timestamp: new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }),
          isFallback: true,
        },
      ]);
    } finally {
      setLoading(false);
    }
  };

  const quickPrompts = [
    'Quy trình đối soát 3 bên hoạt động thế nào?',
    'Tại sao hóa đơn INV-2026-8803 bị phát hiện sai lệch?',
    'Tôi là người mới, hãy hướng dẫn các bước sử dụng web.',
    'Biên độ dung sai (Tolerance Policy) là gì?',
  ];

  return (
    <>
      {/* Floating Trigger Button */}
      {!isOpen && (
        <button
          onClick={() => setIsOpen(true)}
          className="fixed bottom-6 right-6 z-40 bg-gradient-to-r from-indigo-600 to-blue-600 text-white p-3.5 rounded-full shadow-2xl hover:scale-105 hover:shadow-indigo-500/25 transition-all flex items-center gap-2 group cursor-pointer border border-white/20"
          title="Mở Trợ lý AI OpsCopilot"
        >
          <div className="relative">
            <Sparkles className="w-5 h-5 animate-pulse text-amber-300" />
            <span className="absolute -top-1 -right-1 w-2.5 h-2.5 bg-emerald-400 rounded-full border-2 border-slate-900"></span>
          </div>
          <span className="text-xs font-bold pr-1 hidden sm:inline">Trợ lý AI Copilot</span>
        </button>
      )}

      {/* Floating Chat Modal */}
      {isOpen && (
        <div className="fixed bottom-6 right-6 z-50 w-[92vw] sm:w-[420px] h-[580px] bg-white rounded-2xl shadow-2xl border border-slate-200 flex flex-col overflow-hidden animate-in fade-in slide-in-from-bottom-5 duration-200">
          {/* Header */}
          <div className="bg-gradient-to-r from-indigo-900 via-indigo-800 to-slate-900 text-white p-3.5 flex items-center justify-between shadow-xs">
            <div className="flex items-center gap-2.5">
              <div className="w-8 h-8 rounded-xl bg-indigo-500/30 border border-indigo-400/40 flex items-center justify-center text-amber-300">
                <Bot className="w-4 h-4" />
              </div>
              <div>
                <h3 className="font-bold text-xs flex items-center gap-1.5">
                  <span>OpsBridge AI Copilot</span>
                  <span className="text-[10px] bg-emerald-500/20 text-emerald-300 px-1.5 py-0.2 rounded font-normal border border-emerald-400/30">
                    Gemini Live
                  </span>
                </h3>
                <p className="text-[10px] text-slate-300">Chuyên gia đối soát kho & hóa đơn</p>
              </div>
            </div>
            <div className="flex items-center gap-1">
              <button
                onClick={() => setShowSettings(!showSettings)}
                className="p-1.5 rounded-lg text-slate-300 hover:text-white hover:bg-white/10 transition-colors"
                title="Cài đặt API Key"
              >
                <Settings className="w-4 h-4" />
              </button>
              <button
                onClick={() => setIsOpen(false)}
                className="p-1.5 rounded-lg text-slate-300 hover:text-white hover:bg-white/10 transition-colors"
                title="Đóng cửa sổ"
              >
                <X className="w-4 h-4" />
              </button>
            </div>
          </div>

          {/* Settings Sub-panel */}
          {showSettings && (
            <div className="p-3 bg-slate-50 border-b border-slate-200 text-xs space-y-2">
              <div className="flex justify-between items-center">
                <span className="font-bold text-slate-800 flex items-center gap-1">
                  <Settings className="w-3.5 h-3.5 text-indigo-600" />
                  Cấu hình Gemini API Key
                </span>
                <span className="text-[10px] text-slate-500">Google Cloud</span>
              </div>
              <Input
                type="password"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder="Nhập Gemini API Key (AIzaSy...)"
                className="text-xs font-mono"
              />
              <div className="p-2 bg-amber-50 rounded border border-amber-200 text-[11px] text-amber-800 space-y-1">
                <div className="flex items-center gap-1 font-semibold">
                  <ShieldAlert className="w-3.5 h-3.5 text-amber-600" />
                  Lưu ý về Google Cloud Key:
                </div>
                <p>
                  Key cần được bật <strong>Generative Language API</strong> trên Cloud Console của dự án 791168472055. Hệ thống vẫn có sẵn bộ não suy luận đối soát dự phòng nếu key bị hạn chế.
                </p>
              </div>
              <div className="flex justify-end gap-2 pt-1">
                <Button size="sm" variant="outline" onClick={() => setShowSettings(false)}>
                  Đóng
                </Button>
                <Button size="sm" variant="primary" onClick={() => handleSaveKey(apiKey)}>
                  Lưu Key
                </Button>
              </div>
            </div>
          )}

          {/* Message List */}
          <div className="flex-1 p-3.5 overflow-y-auto space-y-3 bg-slate-50/50">
            {messages.map((m) => (
              <div
                key={m.id}
                className={`flex gap-2.5 ${m.sender === 'user' ? 'justify-end' : 'justify-start'}`}
              >
                {m.sender === 'ai' && (
                  <div className="w-7 h-7 rounded-lg bg-indigo-600 text-white flex items-center justify-center shrink-0 mt-0.5">
                    <Bot className="w-4 h-4" />
                  </div>
                )}
                <div
                  className={`p-3 rounded-2xl text-xs max-w-[85%] whitespace-pre-wrap leading-relaxed shadow-xs ${
                    m.sender === 'user'
                      ? 'bg-indigo-600 text-white rounded-br-none'
                      : 'bg-white text-slate-800 border border-slate-200 rounded-bl-none'
                  }`}
                >
                  {m.text}
                  <div
                    className={`text-[9px] mt-1 text-right ${
                      m.sender === 'user' ? 'text-indigo-200' : 'text-slate-400'
                    }`}
                  >
                    {m.timestamp}
                  </div>
                </div>
                {m.sender === 'user' && (
                  <div className="w-7 h-7 rounded-lg bg-slate-200 text-slate-700 flex items-center justify-center shrink-0 mt-0.5">
                    <User className="w-4 h-4" />
                  </div>
                )}
              </div>
            ))}
            {loading && (
              <div className="flex gap-2.5 items-center text-slate-500 text-xs p-2">
                <div className="w-7 h-7 rounded-lg bg-indigo-600 text-white flex items-center justify-center shrink-0">
                  <Bot className="w-4 h-4 animate-spin" />
                </div>
                <span className="italic">AI đang phân tích dữ liệu đối soát...</span>
              </div>
            )}
            <div ref={messagesEndRef} />
          </div>

          {/* Quick Prompts */}
          <div className="px-3 py-2 bg-slate-100 border-t border-slate-200 flex gap-1.5 overflow-x-auto text-[11px] no-scrollbar">
            {quickPrompts.map((q, idx) => (
              <button
                key={idx}
                onClick={() => handleSend(q)}
                className="whitespace-nowrap bg-white border border-slate-300 text-slate-700 hover:border-indigo-500 hover:text-indigo-600 px-2.5 py-1 rounded-full transition-all cursor-pointer shrink-0 font-medium shadow-2xs"
              >
                {q}
              </button>
            ))}
          </div>

          {/* Input Box */}
          <div className="p-2.5 bg-white border-t border-slate-200 flex items-center gap-2">
            <input
              type="text"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && handleSend()}
              placeholder="Hỏi AI về hóa đơn, đơn mua, đối soát..."
              className="flex-1 text-xs px-3 py-2 bg-slate-100 border border-slate-300 rounded-xl focus:outline-none focus:border-indigo-500 focus:bg-white transition-all text-slate-800 placeholder:text-slate-400"
            />
            <Button
              size="sm"
              variant="primary"
              onClick={() => handleSend()}
              disabled={loading || !input.trim()}
              className="bg-indigo-600 hover:bg-indigo-700 h-9 w-9 p-0 flex items-center justify-center rounded-xl shrink-0"
            >
              <Send className="w-4 h-4" />
            </Button>
          </div>
        </div>
      )}
    </>
  );
}
