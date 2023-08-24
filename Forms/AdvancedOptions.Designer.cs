namespace Snacks.Forms
{
    partial class AdvancedOptions
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
            this.encodeDirectoryLabel = new System.Windows.Forms.Label();
            this.outputDirectoryLabel = new System.Windows.Forms.Label();
            this.encodeDirectoryButton = new System.Windows.Forms.Button();
            this.outputDirectoryButton = new System.Windows.Forms.Button();
            this.label2 = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // encodeDirectoryLabel
            // 
            this.encodeDirectoryLabel.AutoEllipsis = true;
            this.encodeDirectoryLabel.AutoSize = true;
            this.encodeDirectoryLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.encodeDirectoryLabel.Location = new System.Drawing.Point(12, 31);
            this.encodeDirectoryLabel.MaximumSize = new System.Drawing.Size(480, 0);
            this.encodeDirectoryLabel.MinimumSize = new System.Drawing.Size(480, 0);
            this.encodeDirectoryLabel.Name = "encodeDirectoryLabel";
            this.encodeDirectoryLabel.Size = new System.Drawing.Size(480, 16);
            this.encodeDirectoryLabel.TabIndex = 0;
            this.encodeDirectoryLabel.Text = "Encode Directory:";
            // 
            // outputDirectoryLabel
            // 
            this.outputDirectoryLabel.AutoEllipsis = true;
            this.outputDirectoryLabel.AutoSize = true;
            this.outputDirectoryLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.outputDirectoryLabel.Location = new System.Drawing.Point(12, 87);
            this.outputDirectoryLabel.MaximumSize = new System.Drawing.Size(480, 0);
            this.outputDirectoryLabel.MinimumSize = new System.Drawing.Size(480, 0);
            this.outputDirectoryLabel.Name = "outputDirectoryLabel";
            this.outputDirectoryLabel.Size = new System.Drawing.Size(480, 16);
            this.outputDirectoryLabel.TabIndex = 1;
            this.outputDirectoryLabel.Text = "Output Directory:";
            // 
            // encodeDirectoryButton
            // 
            this.encodeDirectoryButton.Location = new System.Drawing.Point(15, 50);
            this.encodeDirectoryButton.Name = "encodeDirectoryButton";
            this.encodeDirectoryButton.Size = new System.Drawing.Size(64, 23);
            this.encodeDirectoryButton.TabIndex = 2;
            this.encodeDirectoryButton.Text = "Select";
            this.encodeDirectoryButton.UseVisualStyleBackColor = true;
            // 
            // outputDirectoryButton
            // 
            this.outputDirectoryButton.Location = new System.Drawing.Point(15, 106);
            this.outputDirectoryButton.Name = "outputDirectoryButton";
            this.outputDirectoryButton.Size = new System.Drawing.Size(64, 23);
            this.outputDirectoryButton.TabIndex = 3;
            this.outputDirectoryButton.Text = "Select";
            this.outputDirectoryButton.UseVisualStyleBackColor = true;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(12, 151);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(487, 16);
            this.label2.TabIndex = 4;
            this.label2.Text = "(If you set an output directory, files will not go to their original location aft" +
    "er encode)";
            // 
            // AdvancedOptions
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.ControlLight;
            this.ClientSize = new System.Drawing.Size(510, 177);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.outputDirectoryButton);
            this.Controls.Add(this.encodeDirectoryButton);
            this.Controls.Add(this.outputDirectoryLabel);
            this.Controls.Add(this.encodeDirectoryLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "AdvancedOptions";
            this.ShowIcon = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Snacks - Advanced Options";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label encodeDirectoryLabel;
        private System.Windows.Forms.Label outputDirectoryLabel;
        private System.Windows.Forms.Button encodeDirectoryButton;
        private System.Windows.Forms.Button outputDirectoryButton;
        private System.Windows.Forms.Label label2;
    }
}