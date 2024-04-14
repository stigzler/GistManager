using GistManager.GistService;
using GistManager.ViewModels;
using Octokit;
using Octokit.Internal;
using Syncfusion.Windows.Edit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace GistManager.Utils
{
    internal class CodeEditorManager
    {
        public GistFileViewModel GistFileVM
        {
            get { return gistFileVM; }
            set
            {
                //ensure any residual changes not trigge
                CheckForChangesBeforeGistFileVMChange();

                gistFileVM = value;
                OnGistFileChanged();
            }
        }

        private GistFileViewModel gistFileVM = null;
        private string gistTempFile = null;

        private GistManagerWindowControl mainWindowControl;

        private Dictionary<List<String>, Languages> codeLanguageMappings = new Dictionary<List<string>, Languages>()
            {
            {new List<string>() {"c" }, Languages.C },
            {new List<string>() {"cs" }, Languages.CSharp },
            {new List<string>() {"dpr", "pas", "dfm" }, Languages.Delphi },
            {new List<string>() {"html", "htm" }, Languages.HTML },
            {new List<string>() {"java" }, Languages.Java },
            {new List<string>() {"js", "cjs", "mjs" }, Languages.JScript },
            {new List<string>() {"ps1" }, Languages.PowerShell },
            {new List<string>() {"sql" }, Languages.SQL },
            {new List<string>() {"txt" }, Languages.Text },
            {new List<string>() {"vbs" }, Languages.VBScript },
            {new List<string>() {"vb" }, Languages.VisualBasic },
            {new List<string>() {"xaml" }, Languages.XAML },
            {new List<string>() {"xml" }, Languages.XML },
        };

        public CodeEditorManager(GistManagerWindowControl mainWindowControl)
        {
            this.mainWindowControl = mainWindowControl;
        }

        internal async Task SaveAllAsync()
        {
            //TODO: Meed to reveiw this - buggy
            foreach (GistViewModel gistVM in mainWindowControl.ViewModel.Gists)
            {
                foreach (GistFileViewModel gistFileVM in gistVM.Files)
                {
                    if (gistFileVM.HasChanges)
                    {
                        await UpdateGistOnRepositoryAsync(gistFileVM);
                    }
                }
            }
        }

        private void SetSaveButtonOutline(bool visible)
        {
            if (visible)
                mainWindowControl.SaveButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));
            else
                mainWindowControl.SaveButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        internal void SetGistFileHasChanges(bool isChanged, GistFileViewModel gistFileVM = null)
        {
            if (gistFileVM == null) gistFileVM = this.gistFileVM;

            // this provide visual cue to user that gist has changes
            SetSaveButtonOutline(isChanged);
            gistFileVM.HasChanges = isChanged;
        }
        private void OnGistFileChanged()
        {
            Mouse.OverrideCursor = Cursors.Wait;

            // first delete temporary file of last GistFileView
            if (File.Exists(gistTempFile)) File.Delete(gistTempFile);

            // retrieves HasChanges status of the GitFile and updates the Save BUtton if needed
            SetSaveButtonOutline(GistFileVM.HasChanges);

            // get the parent GIst (i.e. technically the Gist not GistFile.
            // This can be itself as first gistfile alphabetically if classified as the Gist
            GistViewModel gistParentFile = gistFileVM.ParentGist;

            // update the UIControls to the rleevant VM Data
            mainWindowControl.ParentGistName.Text = $"Gist: {gistParentFile.Name}";
            mainWindowControl.ParentGistDescriptionTB.Text = gistParentFile.Description;
            mainWindowControl.GistFilenameTB.Text = $"{gistFileVM.FileName}";

            // Onto loading the code/contents into the code editor
            // need to create a temp file due to the SyntaxEditor control needing files, not stings
            // first check for any old versions and delete
            if (File.Exists(gistTempFile)) File.Delete(gistTempFile);

            // Now update to new gistTempFile
            // get rid of the "gist" prefix"
            gistTempFile = gistFileVM.FileName.Replace("Gist: ", "");

            // replace any illegal chars
            foreach (var c in Path.GetInvalidFileNameChars()) gistTempFile = gistTempFile.Replace(c, '-');

            // construct tempFile's final filename - stored at a clas level so can compare on load of next Gist
            gistTempFile = Path.Combine(Path.GetTempPath(), gistTempFile);

            // write the code to the tmep file
            File.WriteAllText(gistTempFile, gistFileVM.Content);

            // Reset the language
            mainWindowControl.GistCodeEditor.DocumentLanguage = Languages.Text;

            // set the code editor's source to this document
            mainWindowControl.GistCodeEditor.DocumentSource = gistTempFile;

            // now try and auto-math the language form any extension
            string ext = Path.GetExtension(gistTempFile).Replace(".", "");

            if (ext != null)
            {
                var languageKvp = codeLanguageMappings.Where(x => x.Key.Contains(ext)).FirstOrDefault();
                if (!languageKvp.Equals(default(KeyValuePair<List<string>, Languages>)))
                {
                    ChangeEditorLanguage(languageKvp.Value.ToString());
                    mainWindowControl.LanguageSelectorCB.Text = languageKvp.Value.ToString();
                }
            }
            Mouse.OverrideCursor = null;
        }

        internal void ToggleOutline(bool? state)
        {
            mainWindowControl.GistCodeEditor.ShowLineNumber = (bool)state;
            mainWindowControl.GistCodeEditor.EnableOutlining = (bool)state;
        }

        internal void ToggleIntellisense(bool? state)
        {
            mainWindowControl.GistCodeEditor.EnableIntellisense = (bool)state;
        }

        internal void ToggleAutoIndent(bool? state)
        {
            mainWindowControl.GistCodeEditor.IsAutoIndentationEnabled = (bool)state;
        }

        /// <summary>
        /// Updates the Gist on the Gist Repository
        /// </summary>
        /// <returns></returns>
        internal async Task<bool> UpdateGistOnRepositoryAsync(GistFileViewModel gistFileViewModel = null)
        {
           
            if (gistFileViewModel == null) gistFileViewModel = this.gistFileVM;
            if (gistFileViewModel == null) return false;

            // return save button border to normal (aesthetics) 
            SetSaveButtonOutline(false);

            // tmep store 'old' filename
            string oldFilename = gistFileViewModel.OldFilename;

            // update GistFileViewModel for edge cases where changes not caught by UI changes
            UpdateGistViewModel();

            // disable Save button
            mainWindowControl.SaveButton.IsEnabled = false;

            // do repo update
            Gist returnedGist = await mainWindowControl.ViewModel.gistClientService.RenameGistFileAsync(gistFileViewModel.ParentGist.Gist.Id,
                oldFilename, gistFileViewModel.FileName, gistFileViewModel.Content, gistFileViewModel.ParentGist.Description);


            // do UI Element Updates
            // first, displayed filenames and updaqte the raw URL
            GistViewModel uiGistParent = mainWindowControl.ViewModel.Gists.Where(g => g.Gist.Id == gistFileViewModel.ParentGist.Gist.Id).First();

            //GistFileViewModel viewModelGistFile = uiGistParent.Files.Where(gf => gf.FileName == gistFileViewModel.FileName).FirstOrDefault();
            //viewModelGistFile.FileName = gistFileViewModel.FileName;
            //viewModelGistFile.Url = UrlFromGistFileVM(returnedGist, gistFileViewModel.FileName);

            // Test: 1 - whether the new filename means is no longer at top of files list alphabetically (means Lead Gist becomes this GistFile)
            // 2 - if above not true - tests if this gist is the lead gist
            // Result - if either met - do a full refresh as cannot figure how to update manually with this bonkers code. 
            var dave = gistFileViewModel.ParentGist.Files.OrderBy(gf => gf.FileName);
            if (gistFileViewModel.ParentGist.Name != dave.First().GistFile.Name ||
                gistFileViewModel.GistFile.Name == gistFileViewModel.ParentGist.Name)
            {
                gistFileViewModel.ParentGist.Name = gistFileViewModel.FileName;
                mainWindowControl.ViewModel.RefreshCommand.Execute(null);
            }       

            // enable Save button
            mainWindowControl.SaveButton.IsEnabled = true;

            // resets gist file has changes indicator
            SetGistFileHasChanges(false, gistFileViewModel);

            mainWindowControl.GistCodeEditor.SaveFile(gistTempFile);

            return true;
        }

        private void CheckForChangesBeforeGistFileVMChange()
        {
            CheckUiWithGistVmForChanges();
        }

        private void UpdateGistViewModel()
        {
            if (gistFileVM == null) return;

            // update the Gist's viewmodel form the UIElements
            GistFileVM.ParentGist.Description = mainWindowControl.ParentGistDescriptionTB.Text;
            GistFileVM.Content = mainWindowControl.GistCodeEditor.Text;
            GistFileVM.OldFilename = gistFileVM.FileName;
            GistFileVM.FileName = mainWindowControl.GistFilenameTB.Text;
            
            // URL
            //string primaryGistID = GistFileVM.ParentGist.Gist.Id;
            //string primaryGistLastestVersion = GistFileVM.ParentGist.History[0].Version;

            //Uri uri = new Uri(GistFileVM.Url);

            //string[] urlSegments = uri.Segments;

            //StringBuilder urlSb = new StringBuilder();
            //urlSb.Append(uri.Scheme + "://");
            //urlSb.Append(uri.Host + "/");
            //urlSb.Append(urlSegments[1]); // username
            //urlSb.Append(primaryGistID + "/"); // primary Gist Id
            //urlSb.Append("raw/"); // convention
            //urlSb.Append(primaryGistLastestVersion + "/"); // latest primary gist version
            //urlSb.Append(Uri.EscapeUriString(GistFileVM.FileName)).Replace("%20","%2520");

            //GistFileVM.Url = urlSb.ToString();
        }

        private string UrlFromGistFileVM(Gist gist, string filename)
        {
            string primaryGistID = gist.Id;
            string primaryGistLastestVersion = gist.History[0].Version;

            Uri uri = new Uri(gist.HtmlUrl);

            string[] urlSegments = uri.Segments;

            StringBuilder urlSb = new StringBuilder();
            urlSb.Append(uri.Scheme + "://");
            urlSb.Append("gist.githubusercontent.com/");
            urlSb.Append(urlSegments[1]); // username
            urlSb.Append(primaryGistID + "/"); // primary Gist Id
            urlSb.Append("raw/"); // convention
            urlSb.Append(primaryGistLastestVersion + "/"); // latest primary gist version
            urlSb.Append(Uri.EscapeUriString(filename).Replace("%20","%2520"));

            return urlSb.ToString();
        }

        internal void CheckUiWithGistVmForChanges()
        {
            if (GistFileVM == null) return;

            // checks the UIElement values against the view model for changes
            if (mainWindowControl.GistCodeEditor.Text != GistFileVM.Content ||
                mainWindowControl.GistFilenameTB.Text != GistFileVM.FileName ||
               mainWindowControl.ParentGistDescriptionTB.Text != GistFileVM.ParentGist.Description)
            {
                // changes found - do Gist ViewModel update  
                UpdateGistViewModel();

                // set gist file has changes indicator to true
                SetGistFileHasChanges(true);
            }
        }

        internal void ChangeEditorLanguage(string languageString)
        {
            Languages language = (Languages)Enum.Parse(typeof(Languages), languageString);
            mainWindowControl.GistCodeEditor.DocumentLanguage = language;
        }





    }
}
