namespace Snacks
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.startButton = new System.Windows.Forms.Button();
            this.workLocationLabel = new System.Windows.Forms.Label();
            this.openFileDialog1 = new System.Windows.Forms.OpenFileDialog();
            this.menuStrip = new System.Windows.Forms.MenuStrip();
            this.selectFolderToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openFileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openFolderToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.logLabel = new System.Windows.Forms.Label();
            this.logTextBox = new System.Windows.Forms.RichTextBox();
            this.previewBox = new System.Windows.Forms.PictureBox();
            this.previewLabel = new System.Windows.Forms.Label();
            this.convertAudioBox = new System.Windows.Forms.CheckBox();
            this.optionsBox = new System.Windows.Forms.GroupBox();
            this.useNvidiaBox = new System.Windows.Forms.CheckBox();
            this.removeAudioBox = new System.Windows.Forms.CheckBox();
            this.removeSubtitlesBox = new System.Windows.Forms.CheckBox();
            this.deleteFilesBox = new System.Windows.Forms.CheckBox();
            this.helpToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStrip.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.previewBox)).BeginInit();
            this.optionsBox.SuspendLayout();
            this.SuspendLayout();
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(12, 415);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(516, 23);
            this.progressBar.Step = 1;
            this.progressBar.TabIndex = 0;
            // 
            // startButton
            // 
            this.startButton.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.startButton.Location = new System.Drawing.Point(398, 361);
            this.startButton.Name = "startButton";
            this.startButton.Size = new System.Drawing.Size(110, 48);
            this.startButton.TabIndex = 1;
            this.startButton.Text = "Start";
            this.startButton.UseVisualStyleBackColor = true;
            // 
            // workLocationLabel
            // 
            this.workLocationLabel.AutoEllipsis = true;
            this.workLocationLabel.AutoSize = true;
            this.workLocationLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.workLocationLabel.Location = new System.Drawing.Point(9, 35);
            this.workLocationLabel.MaximumSize = new System.Drawing.Size(360, 20);
            this.workLocationLabel.MinimumSize = new System.Drawing.Size(360, 20);
            this.workLocationLabel.Name = "workLocationLabel";
            this.workLocationLabel.Size = new System.Drawing.Size(360, 20);
            this.workLocationLabel.TabIndex = 2;
            this.workLocationLabel.Text = "Folder: [unselected]";
            // 
            // openFileDialog1
            // 
            this.openFileDialog1.FileName = "openFileDialog1";
            // 
            // menuStrip
            // 
            this.menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.selectFolderToolStripMenuItem,
            this.helpToolStripMenuItem});
            this.menuStrip.Location = new System.Drawing.Point(0, 0);
            this.menuStrip.Name = "menuStrip";
            this.menuStrip.Size = new System.Drawing.Size(540, 24);
            this.menuStrip.TabIndex = 4;
            this.menuStrip.Text = "menuStrip1";
            // 
            // selectFolderToolStripMenuItem
            // 
            this.selectFolderToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.openFileToolStripMenuItem,
            this.openFolderToolStripMenuItem});
            this.selectFolderToolStripMenuItem.Name = "selectFolderToolStripMenuItem";
            this.selectFolderToolStripMenuItem.Size = new System.Drawing.Size(37, 20);
            this.selectFolderToolStripMenuItem.Text = "File";
            // 
            // openFileToolStripMenuItem
            // 
            this.openFileToolStripMenuItem.Name = "openFileToolStripMenuItem";
            this.openFileToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.openFileToolStripMenuItem.Text = "Open File";
            // 
            // openFolderToolStripMenuItem
            // 
            this.openFolderToolStripMenuItem.Name = "openFolderToolStripMenuItem";
            this.openFolderToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.openFolderToolStripMenuItem.Text = "Open Folder";
            // 
            // logLabel
            // 
            this.logLabel.AutoSize = true;
            this.logLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.logLabel.Location = new System.Drawing.Point(12, 251);
            this.logLabel.Name = "logLabel";
            this.logLabel.Size = new System.Drawing.Size(33, 16);
            this.logLabel.TabIndex = 5;
            this.logLabel.Text = "Log:";
            // 
            // logTextBox
            // 
            this.logTextBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.logTextBox.Location = new System.Drawing.Point(12, 270);
            this.logTextBox.Name = "logTextBox";
            this.logTextBox.Size = new System.Drawing.Size(362, 139);
            this.logTextBox.TabIndex = 6;
            this.logTextBox.Text = "";
            this.logTextBox.WordWrap = false;
            // 
            // previewBox
            // 
            this.previewBox.BackColor = System.Drawing.Color.Black;
            this.previewBox.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.previewBox.Location = new System.Drawing.Point(383, 54);
            this.previewBox.Name = "previewBox";
            this.previewBox.Size = new System.Drawing.Size(145, 145);
            this.previewBox.TabIndex = 7;
            this.previewBox.TabStop = false;
            // 
            // previewLabel
            // 
            this.previewLabel.AutoSize = true;
            this.previewLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.previewLabel.Location = new System.Drawing.Point(380, 35);
            this.previewLabel.Name = "previewLabel";
            this.previewLabel.Size = new System.Drawing.Size(58, 16);
            this.previewLabel.TabIndex = 9;
            this.previewLabel.Text = "Preview:";
            // 
            // convertAudioBox
            // 
            this.convertAudioBox.AutoSize = true;
            this.convertAudioBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.convertAudioBox.Location = new System.Drawing.Point(6, 21);
            this.convertAudioBox.Name = "convertAudioBox";
            this.convertAudioBox.Size = new System.Drawing.Size(184, 20);
            this.convertAudioBox.TabIndex = 10;
            this.convertAudioBox.Text = "Convert 5.1 audio to stereo";
            this.convertAudioBox.UseVisualStyleBackColor = true;
            // 
            // optionsBox
            // 
            this.optionsBox.Controls.Add(this.useNvidiaBox);
            this.optionsBox.Controls.Add(this.removeAudioBox);
            this.optionsBox.Controls.Add(this.removeSubtitlesBox);
            this.optionsBox.Controls.Add(this.deleteFilesBox);
            this.optionsBox.Controls.Add(this.convertAudioBox);
            this.optionsBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.optionsBox.Location = new System.Drawing.Point(12, 69);
            this.optionsBox.Name = "optionsBox";
            this.optionsBox.Size = new System.Drawing.Size(358, 175);
            this.optionsBox.TabIndex = 12;
            this.optionsBox.TabStop = false;
            this.optionsBox.Text = "Options";
            // 
            // useNvidiaBox
            // 
            this.useNvidiaBox.AutoSize = true;
            this.useNvidiaBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.useNvidiaBox.Location = new System.Drawing.Point(6, 125);
            this.useNvidiaBox.Name = "useNvidiaBox";
            this.useNvidiaBox.Size = new System.Drawing.Size(237, 20);
            this.useNvidiaBox.TabIndex = 14;
            this.useNvidiaBox.Text = "Use hardware acceleration (Nvidia)";
            this.useNvidiaBox.UseVisualStyleBackColor = true;
            // 
            // removeAudioBox
            // 
            this.removeAudioBox.AutoSize = true;
            this.removeAudioBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.removeAudioBox.Location = new System.Drawing.Point(6, 73);
            this.removeAudioBox.Name = "removeAudioBox";
            this.removeAudioBox.Size = new System.Drawing.Size(188, 20);
            this.removeAudioBox.TabIndex = 13;
            this.removeAudioBox.Text = "Remove non-English audio";
            this.removeAudioBox.UseVisualStyleBackColor = true;
            // 
            // removeSubtitlesBox
            // 
            this.removeSubtitlesBox.AutoSize = true;
            this.removeSubtitlesBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.removeSubtitlesBox.Location = new System.Drawing.Point(6, 99);
            this.removeSubtitlesBox.Name = "removeSubtitlesBox";
            this.removeSubtitlesBox.Size = new System.Drawing.Size(203, 20);
            this.removeSubtitlesBox.TabIndex = 12;
            this.removeSubtitlesBox.Text = "Remove non-English subtitles";
            this.removeSubtitlesBox.UseVisualStyleBackColor = true;
            // 
            // deleteFilesBox
            // 
            this.deleteFilesBox.AutoSize = true;
            this.deleteFilesBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.deleteFilesBox.Location = new System.Drawing.Point(6, 47);
            this.deleteFilesBox.Name = "deleteFilesBox";
            this.deleteFilesBox.Size = new System.Drawing.Size(191, 20);
            this.deleteFilesBox.TabIndex = 11;
            this.deleteFilesBox.Text = "Delete old files after convert";
            this.deleteFilesBox.UseVisualStyleBackColor = true;
            // 
            // helpToolStripMenuItem
            // 
            this.helpToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.aboutToolStripMenuItem});
            this.helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            this.helpToolStripMenuItem.Size = new System.Drawing.Size(44, 20);
            this.helpToolStripMenuItem.Text = "Help";
            // 
            // aboutToolStripMenuItem
            // 
            this.aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            this.aboutToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.aboutToolStripMenuItem.Text = "About";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.ControlDark;
            this.ClientSize = new System.Drawing.Size(540, 450);
            this.Controls.Add(this.optionsBox);
            this.Controls.Add(this.previewLabel);
            this.Controls.Add(this.previewBox);
            this.Controls.Add(this.logTextBox);
            this.Controls.Add(this.logLabel);
            this.Controls.Add(this.workLocationLabel);
            this.Controls.Add(this.startButton);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.menuStrip);
            this.DoubleBuffered = true;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.menuStrip;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Snacks";
            this.menuStrip.ResumeLayout(false);
            this.menuStrip.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.previewBox)).EndInit();
            this.optionsBox.ResumeLayout(false);
            this.optionsBox.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.Button startButton;
        private System.Windows.Forms.Label workLocationLabel;
        private System.Windows.Forms.OpenFileDialog openFileDialog1;
        private System.Windows.Forms.MenuStrip menuStrip;
        private System.Windows.Forms.ToolStripMenuItem selectFolderToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem openFolderToolStripMenuItem;
        private System.Windows.Forms.Label logLabel;
        private System.Windows.Forms.RichTextBox logTextBox;
        private System.Windows.Forms.PictureBox previewBox;
        private System.Windows.Forms.Label previewLabel;
        private System.Windows.Forms.CheckBox convertAudioBox;
        private System.Windows.Forms.GroupBox optionsBox;
        private System.Windows.Forms.CheckBox removeAudioBox;
        private System.Windows.Forms.CheckBox removeSubtitlesBox;
        private System.Windows.Forms.CheckBox deleteFilesBox;
        private System.Windows.Forms.ToolStripMenuItem openFileToolStripMenuItem;
        private System.Windows.Forms.CheckBox useNvidiaBox;
        private System.Windows.Forms.ToolStripMenuItem helpToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem aboutToolStripMenuItem;
    }
}