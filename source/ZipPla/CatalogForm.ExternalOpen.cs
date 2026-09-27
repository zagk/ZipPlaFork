// CatalogForm.ExternalOpen.cs
// d1: CatalogForm.cs 는 34,000 줄이 넘어서 리뷰/수정 비용이 큽니다. 우선 포크가 추가한
//     "외부에서 연 경로 처리" 영역만 partial 클래스로 분리했습니다. CatalogForm.cs 는
//     이제 이 파일과 같은 클래스의 일부입니다(동작 변화 없음).
//     나머지 영역(메타데이터 패널, 단일 창/컨텍스트 메뉴)도 같은 방식으로 하나씩 옮기면
//     컴파일 결과는 그대로 두고 파일만 작아집니다.

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace ZipPla
{
    public partial class CatalogForm
    {
        /// <summary>
        /// Adds a path supplied by an external application (for example Windows Explorer's
        /// context menu) to the left Add/bookmark list and persists the list.
        /// </summary>
        private void AddExternalPathToAddList(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!(Directory.Exists(path) || File.Exists(path))) return;
                path = NormalizeBookmarkPath(path);

                // Older builds could leave an existing external bookmark duplicated when a
                // second Explorer context-menu request caused the bookmark configuration to be
                // loaded again. Clean exact-location duplicates before adding the new path.
                bool changed = RemoveDuplicateBookmarksByLocation();

                // Do not add the same location twice.
                foreach (DataGridViewRow row in dgvDirectoryList.Rows)
                {
                    var bookmark = row.Cells[tbcDirectoryName.Index].Value as ColoredBookmark;
                    var location = bookmark?.SimpleBookmark?.Location;
                    if (!string.IsNullOrEmpty(location) &&
                        string.Equals(NormalizeBookmarkPath(location), path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (changed)
                        {
                            saveBookmarkToIni_bookmarkChanged = true;
                            saveBookmarkToConfig();
                        }
                        return;
                    }
                }

                addCatalogBookmarkToList(currentConditionToColoredBookmark(currentProfileColor, path));
                saveBookmarkToIni_bookmarkChanged = true;
                saveBookmarkToConfig();
            }
            catch (Exception ex)
            {
                // d1: 예전에는 완전히 비어 있던 catch 였다. 경로를 여는 동작은 계속 진행하되 기록은 남긴다.
                Program.LogException(ex, "CatalogForm.AddExternalPathToAddList");
            }
        }

        private static string NormalizeBookmarkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Program.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private bool RemoveDuplicateBookmarksByLocation()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remove = new List<DataGridViewRow>();

            foreach (DataGridViewRow row in dgvDirectoryList.Rows)
            {
                var bookmark = row.Cells[tbcDirectoryName.Index].Value as ColoredBookmark;
                var location = bookmark?.SimpleBookmark?.Location;
                if (string.IsNullOrWhiteSpace(location)) continue;

                var normalized = NormalizeBookmarkPath(location);
                if (string.IsNullOrEmpty(normalized)) continue;

                if (!seen.Add(normalized)) remove.Add(row);
            }

            for (var i = remove.Count - 1; i >= 0; i--)
            {
                var row = remove[i];
                if (!row.IsNewRow && row.Index >= 0 && row.Index < dgvDirectoryList.Rows.Count)
                    dgvDirectoryList.Rows.RemoveAt(row.Index);
            }
            return remove.Count > 0;
        }

        /// <summary>
        /// Opens a folder or file requested by another process (see SingleInstanceManager, used
        /// by the "Only one window" Start-menu option) inside this already-running window,
        /// instead of that process opening a brand new window.
        ///
        /// Deliberately uses the same runtime navigation path normal in-app navigation already
        /// uses (setting zabLocation.Text and calling MakePreview()) rather than LoadSettings(),
        /// because LoadSettings() resets window size/position/maximized state and wires one-time
        /// event handlers -- it is only safe to call once, during construction/startup.
        /// </summary>
        public void OpenExternalPath(string path)
        {
            try
            {
                path = Program.GetFullPath(path);

                string folder;
                string selectedFileName = null;

                if (Directory.Exists(path))
                {
                    folder = path;
                }
                else if (File.Exists(path))
                {
                    folder = Path.GetDirectoryName(path);
                    selectedFileName = getFileName(path);
                }
                else
                {
                    return;
                }

                if (string.IsNullOrEmpty(folder)) return;

                zabLocation.Text = folder;
                if (selectedFileName != null)
                {
                    MakePreview(selectedIndex: -1, selectedFileName: selectedFileName);
                }
                else
                {
                    MakePreview();
                }

                AddExternalPathToAddList(path);
            }
            catch (Exception ex)
            {
                Program.AlertError(ex);
            }
        }
    }
}
