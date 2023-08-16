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
            this.helpToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.logLabel = new System.Windows.Forms.Label();
            this.logTextBox = new System.Windows.Forms.RichTextBox();
            this.previewBox = new System.Windows.Forms.PictureBox();
            this.previewLabel = new System.Windows.Forms.Label();
            this.convertAudioBox = new System.Windows.Forms.CheckBox();
            this.optionsBox = new System.Windows.Forms.GroupBox();
            this.removeAudioBox = new System.Windows.Forms.CheckBox();
            this.removeSubtitlesBox = new System.Windows.Forms.CheckBox();
            this.deleteFilesBox = new System.Windows.Forms.CheckBox();
            this.encoderBox = new System.Windows.Forms.ComboBox();
            this.encoderLabel = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.targetBitrateBox = new System.Windows.Forms.ComboBox();
            this.filesRemainingLabel = new System.Windows.Forms.Label();
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
            this.openFileToolStripMenuItem.Size = new System.Drawing.Size(139, 22);
            this.openFileToolStripMenuItem.Text = "Open File";
            // 
            // openFolderToolStripMenuItem
            // 
            this.openFolderToolStripMenuItem.Name = "openFolderToolStripMenuItem";
            this.openFolderToolStripMenuItem.Size = new System.Drawing.Size(139, 22);
            this.openFolderToolStripMenuItem.Text = "Open Folder";
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
            this.aboutToolStripMenuItem.Size = new System.Drawing.Size(107, 22);
            this.aboutToolStripMenuItem.Text = "About";
            // 
            // logLabel
            // 
            this.logLabel.AutoSize = true;
            this.logLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.logLabel.Location = new System.Drawing.Point(15, 231);
            this.logLabel.Name = "logLabel";
            this.logLabel.Size = new System.Drawing.Size(33, 16);
            this.logLabel.TabIndex = 5;
            this.logLabel.Text = "Log:";
            // 
            // logTextBox
            // 
            this.logTextBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.logTextBox.Location = new System.Drawing.Point(12, 250);
            this.logTextBox.Name = "logTextBox";
            this.logTextBox.Size = new System.Drawing.Size(362, 159);
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
            this.previewLabel.Size = new System.Drawing.Size(99, 16);
            this.previewLabel.TabIndex = 9;
            this.previewLabel.Text = "Preview Image:";
            // 
            // convertAudioBox
            // 
            this.convertAudioBox.AutoSize = true;
            this.convertAudioBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.convertAudioBox.Location = new System.Drawing.Point(6, 21);
            this.convertAudioBox.Name = "convertAudioBox";
            this.convertAudioBox.Size = new System.Drawing.Size(134, 20);
            this.convertAudioBox.TabIndex = 10;
            this.convertAudioBox.Text = "Stereo Audio Only";
            this.convertAudioBox.UseVisualStyleBackColor = true;
            // 
            // optionsBox
            // 
            this.optionsBox.Controls.Add(this.removeAudioBox);
            this.optionsBox.Controls.Add(this.removeSubtitlesBox);
            this.optionsBox.Controls.Add(this.deleteFilesBox);
            this.optionsBox.Controls.Add(this.convertAudioBox);
            this.optionsBox.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.optionsBox.Location = new System.Drawing.Point(12, 64);
            this.optionsBox.Name = "optionsBox";
            this.optionsBox.Size = new System.Drawing.Size(220, 164);
            this.optionsBox.TabIndex = 12;
            this.optionsBox.TabStop = false;
            this.optionsBox.Text = "Options";
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
            // encoderBox
            // 
            this.encoderBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.encoderBox.FormattingEnabled = true;
            this.encoderBox.Items.AddRange(new object[] {
            "Software",
            "Intel QuickSync",
            "Nvidia",
            "AMD/Radeon"});
            this.encoderBox.Location = new System.Drawing.Point(241, 83);
            this.encoderBox.Name = "encoderBox";
            this.encoderBox.Size = new System.Drawing.Size(121, 21);
            this.encoderBox.TabIndex = 15;
            // 
            // encoderLabel
            // 
            this.encoderLabel.AutoSize = true;
            this.encoderLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.encoderLabel.Location = new System.Drawing.Point(238, 64);
            this.encoderLabel.Name = "encoderLabel";
            this.encoderLabel.Size = new System.Drawing.Size(61, 16);
            this.encoderLabel.TabIndex = 16;
            this.encoderLabel.Text = "Encoder:";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(238, 115);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(91, 16);
            this.label1.TabIndex = 17;
            this.label1.Text = "Target Bitrate:";
            // 
            // targetBitrateBox
            // 
            this.targetBitrateBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.targetBitrateBox.FormattingEnabled = true;
            this.targetBitrateBox.Items.AddRange(new object[] {
            "1000",
            "1500",
            "2000",
            "2500",
            "3000",
            "3500",
            "4000",
            "4500",
            "5000"});
            this.targetBitrateBox.Location = new System.Drawing.Point(241, 135);
            this.targetBitrateBox.Name = "targetBitrateBox";
            this.targetBitrateBox.Size = new System.Drawing.Size(121, 21);
            this.targetBitrateBox.TabIndex = 18;
            // 
            // filesRemainingLabel
            // 
            this.filesRemainingLabel.AutoSize = true;
            this.filesRemainingLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.filesRemainingLabel.Location = new System.Drawing.Point(380, 212);
            this.filesRemainingLabel.Name = "filesRemainingLabel";
            this.filesRemainingLabel.Size = new System.Drawing.Size(117, 16);
            this.filesRemainingLabel.TabIndex = 19;
            this.filesRemainingLabel.Text = "Files Remaining: 0";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.ControlDark;
            this.ClientSize = new System.Drawing.Size(540, 450);
            this.Controls.Add(this.filesRemainingLabel);
            this.Controls.Add(this.targetBitrateBox);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.encoderBox);
            this.Controls.Add(this.encoderLabel);
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
        private System.Windows.Forms.ToolStripMenuItem helpToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem aboutToolStripMenuItem;
        private System.Windows.Forms.ComboBox encoderBox;
        private System.Windows.Forms.Label encoderLabel;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.ComboBox targetBitrateBox;
        private System.Windows.Forms.Label filesRemainingLabel;
    }
}