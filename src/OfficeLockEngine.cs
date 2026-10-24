using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CadLockPlugin
{
    /// <summary>
    /// Word / Excel 文档的批量保护：
    /// Excel 清除数字签名、公式转为固定值、保护工作表(不可编辑文字与数字)；
    /// Word 清除数字签名、设为只读保护(不可编辑文字与数字)。
    /// 通过晚绑定调用 Office COM，编译期不依赖 Office Interop 程序集。
    /// </summary>
    public static class OfficeLockEngine
    {
        /// <summary>工作表/文档保护口令，避免对方一键取消保护</summary>
        public const string ProtectPassword = "CadLock@2024";

        private const string ExcelSuffix = "_lock";
        private const string UnlockSuffix = "_unlock";

        public static bool IsDwgFile(string path)
        {
            return string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsOfficeFile(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".xlsx":
                case ".xlsm":
                case ".xls":
                case ".docx":
                case ".docm":
                case ".doc":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsWordFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".docx" || ext == ".docm" || ext == ".doc";
        }

        // ---------------------------------------------------------------- 批量加密 / 解锁

        public static void BatchProtectFiles(IEnumerable<string> files, CadLockOptions options, Action<string, BatchLockResult, string>? progress = null)
        {
            if (files == null || options == null) return;

            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    progress?.Invoke(file ?? string.Empty, BatchLockResult.Failed, "文件不存在");
                    continue;
                }

                try
                {
                    string message = IsWordFile(file)
                        ? ProtectWord(file, options.ReplaceOriginalFile)
                        : ProtectExcel(file, options.ReplaceOriginalFile);

                    progress?.Invoke(file, BatchLockResult.Success, message);
                }
                catch (Exception ex)
                {
                    progress?.Invoke(file, BatchLockResult.Failed, Describe(ex));
                }
            }
        }

        public static void BatchUnprotectFiles(IEnumerable<string> files, CadLockOptions options, Action<string, BatchLockResult, string>? progress = null)
        {
            if (files == null || options == null) return;

            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    progress?.Invoke(file ?? string.Empty, BatchLockResult.Failed, "文件不存在");
                    continue;
                }

                try
                {
                    string message = IsWordFile(file)
                        ? UnprotectWord(file, options.ReplaceOriginalFile)
                        : UnprotectExcel(file, options.ReplaceOriginalFile);

                    progress?.Invoke(file, message == string.Empty ? BatchLockResult.Skipped : BatchLockResult.Success,
                        message == string.Empty ? "文档未被保护，无需解锁" : message);
                }
                catch (Exception ex)
                {
                    progress?.Invoke(file, BatchLockResult.Failed, Describe(ex));
                }
            }
        }

        // ---------------------------------------------------------------- Excel

        private static string ProtectExcel(string file, bool replaceOriginal)
        {
            dynamic app = CreateApplication("Excel.Application", "Excel");
            dynamic? book = null;

            try
            {
                app.Visible = false;
                app.DisplayAlerts = false;

                book = app.Workbooks.Open(file);

                int signatures = RemoveExcelSignatures(book);
                int formulaSheets = 0;
                int protectedSheets = 0;
                int headerFooters = 0;

                foreach (dynamic sheet in book.Worksheets)
                {
                    int converted = ConvertFormulasToValues(sheet);
                    if (converted > 0) formulaSheets++;

                    // 清除页眉页脚里的签名（文字与 &G 图片引用）
                    headerFooters += ClearExcelHeaderFooter(sheet);

                    try
                    {
                        if (!Convert.ToBoolean(sheet.ProtectContents))
                        {
                            sheet.Protect(ProtectPassword, true, true);
                            protectedSheets++;
                        }
                    }
                    catch
                    {
                        // 保护失败时继续
                    }
                }

                // 保护工作簿结构，禁止增删/重命名工作表
                try
                {
                    if (!Convert.ToBoolean(book.ProtectStructure))
                    {
                        book.Protect(ProtectPassword, true, true);
                    }
                }
                catch
                {
                    // 忽略
                }

                if (replaceOriginal)
                {
                    book.Save();
                    return BuildExcelMessage(signatures, formulaSheets, protectedSheets, headerFooters, true);
                }

                string target = GetUniquePath(file, ExcelSuffix);
                book.SaveCopyAs(target);
                return target;
            }
            finally
            {
                CloseExcel(app, book);
            }
        }

        private static string UnprotectExcel(string file, bool replaceOriginal)
        {
            dynamic app = CreateApplication("Excel.Application", "Excel");
            dynamic? book = null;

            try
            {
                app.Visible = false;
                app.DisplayAlerts = false;

                book = app.Workbooks.Open(file);

                bool changed = false;

                try
                {
                    book.Unprotect(ProtectPassword);
                    changed = true;
                }
                catch
                {
                    // 未设结构保护
                }

                foreach (dynamic sheet in book.Worksheets)
                {
                    try
                    {
                        if (Convert.ToBoolean(sheet.ProtectContents))
                        {
                            sheet.Unprotect(ProtectPassword);
                            changed = true;
                        }
                    }
                    catch
                    {
                        // 忽略单个工作表
                    }
                }

                if (!changed) return string.Empty;

                if (replaceOriginal)
                {
                    book.Save();
                    return "已解除工作表保护并覆盖原文件";
                }

                string target = GetUniquePath(file, UnlockSuffix);
                book.SaveCopyAs(target);
                return target;
            }
            finally
            {
                CloseExcel(app, book);
            }
        }

        /// <summary>
        /// 把公式单元格逐个替换成当前取值，公式本身不再保留。
        /// 不能整块赋值：Value2 返回的数组里含 DBNull，回写会抛类型转换异常。
        /// </summary>
        private static int ConvertFormulasToValues(dynamic sheet)
        {
            int converted = 0;
            try
            {
                // -4123 = xlCellTypeFormulas（注意不是 4，4 是“空单元格”）
                dynamic cells = sheet.UsedRange.SpecialCells(-4123);
                foreach (dynamic cell in cells)
                {
                    try
                    {
                        dynamic value = cell.Value2;
                        cell.Value2 = value;
                        converted++;
                    }
                    catch
                    {
                        // 公式结果无法转值(如错误值)时跳过该单元格
                    }
                }
            }
            catch
            {
                // 工作表没有公式时 SpecialCells 会抛异常，属正常情况
            }
            return converted;
        }

        private static int RemoveExcelSignatures(dynamic book)
        {
            int removed = 0;
            try
            {
                while (Convert.ToInt32(book.Signatures.Count) > 0)
                {
                    book.Signatures(1).Delete();
                    removed++;
                }
            }
            catch
            {
                // 老格式或不支持签名时忽略
            }
            return removed;
        }

        private static string BuildExcelMessage(int signatures, int formulaSheets, int protectedSheets, int headerFooters, bool replaced)
        {
            string message = "已清除数字签名 " + signatures + " 个，页眉页脚签名 " + headerFooters
                             + " 处，公式转值 " + formulaSheets + " 个工作表，保护工作表 " + protectedSheets + " 个（不可编辑）";
            return replaced ? message + "，并已覆盖原文件" : message;
        }

        /// <summary>
        /// 清空工作表的页眉页脚：签名常见形式是页眉里的姓名/公司名文字，或 &G 引用的签名图片
        /// </summary>
        private static int ClearExcelHeaderFooter(dynamic sheet)
        {
            int changed = 0;
            try
            {
                dynamic setup = sheet.PageSetup;
                changed += ClearHeaderFooterPart(setup);

                try
                {
                    changed += ClearHeaderFooterPart(setup.FirstPage);
                }
                catch
                {
                    // 未启用首页不同时忽略
                }

                try
                {
                    changed += ClearHeaderFooterPart(setup.EvenPage);
                }
                catch
                {
                    // 未启用奇偶页不同时忽略
                }
            }
            catch
            {
                // 某些工作表(如图表页)没有 PageSetup
            }

            return changed;
        }

        private static int ClearHeaderFooterPart(dynamic page)
        {
            int changed = 0;

            changed += ClearHeaderFooterText(() => page.LeftHeader, v => page.LeftHeader = v);
            changed += ClearHeaderFooterText(() => page.CenterHeader, v => page.CenterHeader = v);
            changed += ClearHeaderFooterText(() => page.RightHeader, v => page.RightHeader = v);
            changed += ClearHeaderFooterText(() => page.LeftFooter, v => page.LeftFooter = v);
            changed += ClearHeaderFooterText(() => page.CenterFooter, v => page.CenterFooter = v);
            changed += ClearHeaderFooterText(() => page.RightFooter, v => page.RightFooter = v);

            // 清掉页眉页脚里嵌入的签名图片。只清文本的话图片仍留在文件里(xl/media)，
            // 把图片 Filename 置空后再保存，Excel 才会把图片部件一并移除。
            changed += ClearHeaderFooterPicture(() => page.LeftHeaderPicture);
            changed += ClearHeaderFooterPicture(() => page.CenterHeaderPicture);
            changed += ClearHeaderFooterPicture(() => page.RightHeaderPicture);
            changed += ClearHeaderFooterPicture(() => page.LeftFooterPicture);
            changed += ClearHeaderFooterPicture(() => page.CenterFooterPicture);
            changed += ClearHeaderFooterPicture(() => page.RightFooterPicture);

            return changed;
        }

        private static int ClearHeaderFooterPicture(Func<object> getPicture)
        {
            try
            {
                dynamic picture = getPicture();
                string file = Convert.ToString(picture.Filename) ?? string.Empty;
                if (file.Length == 0) return 0;

                picture.Filename = string.Empty;
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        private static int ClearHeaderFooterText(Func<string> read, Action<string> write)
        {
            try
            {
                string current = read() ?? string.Empty;
                if (current.Length == 0) return 0;

                write(string.Empty);
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        private static void CloseExcel(dynamic app, dynamic? book)
        {
            try
            {
                if (book != null) book.Close(false);
            }
            catch
            {
                // 关闭失败忽略
            }

            try
            {
                if (app != null) app.Quit();
            }
            catch
            {
                // 退出失败忽略
            }

            Release((object?)book);
            Release((object?)app);

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch
            {
                // 收尾异常不影响已保存的结果
            }
        }

        // ---------------------------------------------------------------- Word

        private static string ProtectWord(string file, bool replaceOriginal)
        {
            dynamic app = CreateApplication("Word.Application", "Word");
            dynamic? doc = null;

            try
            {
                app.Visible = false;
                app.DisplayAlerts = 0;

                doc = app.Documents.Open(file, false, false);

                int signatures = RemoveWordSignatures(doc);
                int headerFooters = ClearWordHeadersFooters(doc);

                // 3 = wdAllowOnlyReading：只能看，不能改任何文字与数字
                if (Convert.ToInt32(doc.ProtectionType) == -1)
                {
                    doc.Protect(3, true, ProtectPassword);
                }

                string message = "已清除数字签名 " + signatures + " 个、页眉页脚签名 " + headerFooters + " 处，文档已设为只读保护（不可编辑）";

                if (replaceOriginal)
                {
                    doc.Save();
                    return message + "，并已覆盖原文件";
                }

                string target = GetUniquePath(file, ExcelSuffix);
                doc.SaveAs2(target);
                return target;
            }
            finally
            {
                CloseWord(app, doc);
            }
        }

        private static string UnprotectWord(string file, bool replaceOriginal)
        {
            dynamic app = CreateApplication("Word.Application", "Word");
            dynamic? doc = null;

            try
            {
                app.Visible = false;
                app.DisplayAlerts = 0;

                doc = app.Documents.Open(file, false, false);

                if (Convert.ToInt32(doc.ProtectionType) == -1) return string.Empty;

                doc.Unprotect(ProtectPassword);

                if (replaceOriginal)
                {
                    doc.Save();
                    return "已解除只读保护并覆盖原文件";
                }

                string target = GetUniquePath(file, UnlockSuffix);
                doc.SaveAs2(target);
                return target;
            }
            finally
            {
                CloseWord(app, doc);
            }
        }

        /// <summary>
        /// 清空所有节的页眉页脚（文字 + 页眉里的签名图片），链接到上一节的头脚随上一节一起清除
        /// </summary>
        private static int ClearWordHeadersFooters(dynamic doc)
        {
            int changed = 0;

            try
            {
                foreach (dynamic section in doc.Sections)
                {
                    for (int i = 1; i <= 3; i++) // 1=Primary 2=FirstPage 3=EvenPages
                    {
                        changed += ClearWordHeaderFooter(section.Headers[i]);
                        changed += ClearWordHeaderFooter(section.Footers[i]);
                    }
                }
            }
            catch
            {
                // 文档结构异常时忽略
            }

            return changed;
        }

        private static int ClearWordHeaderFooter(dynamic headerFooter)
        {
            try
            {
                // 与上一节链接时内容相同，清上一节即可
                if (Convert.ToBoolean(headerFooter.LinkToPrevious)) return 0;
            }
            catch
            {
                // 忽略
            }

            try
            {
                dynamic range = headerFooter.Range;

                string text = Convert.ToString(range.Text) ?? string.Empty;
                text = text.Trim('\r', '\n', ' ', '\t', '\a', '\u0007');

                int inlineShapes = SafeCount(() => range.InlineShapes.Count);
                int floatingShapes = SafeCount(() => headerFooter.Shapes.Count);

                if (text.Length == 0 && inlineShapes == 0 && floatingShapes == 0) return 0;

                // 删除文字与嵌入式(签名)图片
                range.Delete();

                // 删除浮动的签名图片
                try
                {
                    for (int i = Convert.ToInt32(headerFooter.Shapes.Count); i >= 1; i--)
                    {
                        headerFooter.Shapes[i].Delete();
                    }
                }
                catch
                {
                    // 忽略
                }

                return 1;
            }
            catch
            {
                return 0;
            }
        }

        private static int SafeCount(Func<int> counter)
        {
            try
            {
                return counter();
            }
            catch
            {
                return 0;
            }
        }

        private static int RemoveWordSignatures(dynamic doc)
        {
            int removed = 0;
            try
            {
                while (Convert.ToInt32(doc.Signatures.Count) > 0)
                {
                    doc.Signatures(1).Delete();
                    removed++;
                }
            }
            catch
            {
                // 不支持签名时忽略
            }
            return removed;
        }

        private static void CloseWord(dynamic app, dynamic? doc)
        {
            try
            {
                if (doc != null) doc.Close(0);
            }
            catch
            {
                // 关闭失败忽略
            }

            try
            {
                if (app != null) app.Quit();
            }
            catch
            {
                // 退出失败忽略
            }

            Release((object?)doc);
            Release((object?)app);

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch
            {
                // 收尾异常不影响已保存的结果
            }
        }

        // ---------------------------------------------------------------- 公共

        private static dynamic CreateApplication(string progId, string displayName)
        {
            Type? type = Type.GetTypeFromProgID(progId);
            if (type == null)
            {
                throw new InvalidOperationException("未检测到 " + displayName + "，无法处理该文档；请先安装 Microsoft Office");
            }

            return Activator.CreateInstance(type);
        }

        private static string GetUniquePath(string sourcePath, string suffix)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(sourcePath) + suffix;
            string ext = Path.GetExtension(sourcePath);

            string path = Path.Combine(dir, name + ext);
            int index = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, name + "(" + index + ")" + ext);
                index++;
            }
            return path;
        }

        private static string Describe(Exception ex)
        {
            var com = ex as COMException;
            if (com != null)
            {
                uint code = (uint)com.ErrorCode;
                if (code == 0x80040154) return "未安装 Office 或 COM 组件未注册";
                if (code == 0x80010001 || code == 0x800A03EC) return "文档被占用或内容异常，请关闭 Office 后重试";
            }
            return ex.Message;
        }

        private static void Release(object? comObject)
        {
            try
            {
                if (comObject != null && Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch
            {
                // 释放失败忽略
            }
        }
    }
}
