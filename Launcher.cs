using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HiTessModelBuilder.Launcher
{
  public partial class Form1 : Form
  {
    // UI 컨트롤
    private TextBox txtStru, txtPipe, txtEquip;
    private NumericUpDown numMesh;
    private CheckBox chkUbolt, chkNastran;
    private Button btnRun;
    private Panel pnlOverlay; // 작동 중 오버레이
    private ProgressBar prgStatus;

    public Form1()
    {
      InitializeModernUI();
    }

    private void InitializeModernUI()
    {
      // 1. 폼 기본 설정
      this.Text = "HiTess Model Builder - Professional Launcher";
      // ★ 하단 텍스트 공간 확보를 위해 세로 높이를 520 -> 540으로 살짝 늘렸습니다.
      this.Size = new Size(750, 540);
      this.StartPosition = FormStartPosition.CenterScreen;
      this.FormBorderStyle = FormBorderStyle.FixedDialog;
      this.MaximizeBox = false;
      this.BackColor = Color.FromArgb(240, 243, 249); // 밝은 블루그레이 배경
      this.Font = new Font("Segoe UI", 9.5f);

      // 헤더 섹션
      Panel pnlHeader = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Color.FromArgb(41, 53, 65) };
      Label lblTitle = new Label
      {
        Text = "HiTESS Model Generation Pipeline",
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 16),
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(20, 0, 0, 0)
      };
      pnlHeader.Controls.Add(lblTitle);
      this.Controls.Add(pnlHeader);

      int startY = 90;

      // [Section 1] 데이터 입력 그룹
      GroupBox grpInput = CreateGroup("Data Input Selection (CSV Files)", 20, startY, 695, 185);
      txtStru = AddModernFileRow(grpInput, "Structure (Req.):", 35, out Button b1);
      b1.Click += (s, e) => SelectFile(txtStru);
      txtPipe = AddModernFileRow(grpInput, "Pipe (Opt.):", 80, out Button b2);
      b2.Click += (s, e) => SelectFile(txtPipe);
      txtEquip = AddModernFileRow(grpInput, "Equipment (Opt.):", 125, out Button b3);
      b3.Click += (s, e) => SelectFile(txtEquip);

      // [Section 2] 옵션 설정 그룹
      GroupBox grpOpt = CreateGroup("Execution Parameters", 20, startY + 200, 695, 90);
      grpOpt.Controls.Add(new Label { Text = "Mesh Size (mm):", Bounds = new Rectangle(20, 42, 110, 20) });
      numMesh = new NumericUpDown { Bounds = new Rectangle(130, 40, 80, 25), Minimum = 10, Maximum = 5000, Value = 500 };

      // ★ 이전 요청 반영: 글자가 잘리지 않도록 폭(Width)을 늘리고 X좌표를 조정했습니다.
      chkUbolt = new CheckBox { Text = "Ubolt DOF 6자유도 지정", Bounds = new Rectangle(240, 41, 190, 20), Checked = false };
      chkNastran = new CheckBox { Text = "Nastran을 통한 모델검증 수행", Bounds = new Rectangle(450, 41, 210, 20), Checked = true };

      grpOpt.Controls.AddRange(new Control[] { numMesh, chkUbolt, chkNastran });

      // [Section 3] 실행 버튼
      btnRun = new Button
      {
        Text = "RUN PIPELINE",
        Bounds = new Rectangle(250, 410, 250, 50),
        BackColor = Color.FromArgb(52, 152, 219),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        Cursor = Cursors.Hand
      };
      btnRun.FlatAppearance.BorderSize = 0;
      btnRun.Click += BtnRun_Click;
      this.Controls.Add(btnRun);

      // ========================================================
      // ★ [신규 추가] 사용 기한 안내 라벨 (버튼 바로 아래 중앙 정렬)
      // ========================================================
      Label lblExpire = new Label
      {
        Text = "※ 베타 테스트 버전 (26년 3월 31일까지 사용가능)",
        Bounds = new Rectangle(225, 465, 300, 20),
        ForeColor = Color.DimGray, // 너무 튀지 않게 회색 처리
        Font = new Font("맑은 고딕", 9f),
        TextAlign = ContentAlignment.MiddleCenter
      };
      this.Controls.Add(lblExpire);

      // [Section 4] 작동 중 오버레이 (숨김 상태)
      // 오버레이 높이도 폼 증가분에 맞춰 조금 키워줍니다.
      pnlOverlay = new Panel { Bounds = new Rectangle(0, 70, 750, 470), BackColor = Color.FromArgb(200, 255, 255, 255), Visible = false };
      Label lblWait = new Label
      {
        Text = "Processing Pipeline... Please wait.",
        Font = new Font("Segoe UI Semibold", 13),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Top,
        Height = 250
      };
      prgStatus = new ProgressBar
      {
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 30,
        Bounds = new Rectangle(225, 220, 300, 15)
      };
      pnlOverlay.Controls.AddRange(new Control[] { lblWait, prgStatus });
      this.Controls.Add(pnlOverlay);
      pnlOverlay.BringToFront();
    }

    private GroupBox CreateGroup(string title, int x, int y, int w, int h)
    {
      GroupBox gb = new GroupBox { Text = title, Bounds = new Rectangle(x, y, w, h), BackColor = Color.White };
      this.Controls.Add(gb);
      return gb;
    }

    private TextBox AddModernFileRow(Control parent, string label, int y, out Button btn)
    {
      parent.Controls.Add(new Label { Text = label, Bounds = new Rectangle(20, y + 5, 120, 20), Font = new Font("Segoe UI", 9, FontStyle.Bold) });
      TextBox tb = new TextBox { Bounds = new Rectangle(140, y, 430, 25), ReadOnly = true, BackColor = Color.FromArgb(245, 245, 245) };
      btn = new Button { Text = "Browse", Bounds = new Rectangle(585, y - 1, 85, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.LightGray };
      parent.Controls.Add(tb); parent.Controls.Add(btn);
      return tb;
    }

    private void SelectFile(TextBox tb)
    {
      using (OpenFileDialog ofd = new OpenFileDialog { Filter = "CSV Files (*.csv)|*.csv" })
        if (ofd.ShowDialog() == DialogResult.OK) tb.Text = ofd.FileName;
    }

    private async void BtnRun_Click(object sender, EventArgs e)
    {
      if (string.IsNullOrWhiteSpace(txtStru.Text))
      {
        MessageBox.Show("Structure CSV 파일은 필수입니다.", "입력 누락", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      // 1. UI 작동 중 상태로 변경
      pnlOverlay.Visible = true;
      btnRun.Enabled = false;

      var options = new HiTessModelBuilder.AppOptions
      {
        StruCsvPath = txtStru.Text,
        PipeCsvPath = string.IsNullOrWhiteSpace(txtPipe.Text) ? null : txtPipe.Text,
        EquipCsvPath = string.IsNullOrWhiteSpace(txtEquip.Text) ? null : txtEquip.Text,
        MeshSize = (double)numMesh.Value,
        ForceUboltRigid = chkUbolt.Checked,
        RunNastran = chkNastran.Checked,
        PipelineDebug = true,
        CsvDebug = true,
        FeModelDebug = true
      };

      // 2. 비동기 실행 (Task.Run)
      try
      {
        await Task.Run(() => HiTessModelBuilder.MainApp.RunApplication(options));

        // 3. 작업 완료 후 처리
        pnlOverlay.Visible = false;
        btnRun.Enabled = true;

        // 결과 파일 탐색 (가장 최근에 생성된 로그와 BDF 찾기)
        string targetDir = Path.GetDirectoryName(txtStru.Text);
        string latestLog = Directory.GetFiles(targetDir, "*_ProcessLog_*.txt")
                                    .OrderByDescending(f => File.GetCreationTime(f)).FirstOrDefault();
        string latestBdf = Directory.GetFiles(targetDir, "*.bdf")
                                    .OrderByDescending(f => File.GetCreationTime(f)).FirstOrDefault();

        string finishMsg = $"작업 완료!\n\n저장 경로: {targetDir}\n생성 파일: {Path.GetFileName(latestBdf)}";

        if (latestLog != null) ShowLogResult(latestLog, finishMsg);
        else MessageBox.Show(finishMsg, "성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
      catch (TimeoutException tex)
      {
        // DLL 단에서 날짜가 지나 예외를 던지면 이쪽으로 빠짐
        pnlOverlay.Visible = false;
        btnRun.Enabled = true;
        MessageBox.Show(tex.Message, "기간 만료", MessageBoxButtons.OK, MessageBoxIcon.Stop);
      }
      catch (Exception ex)
      {
        pnlOverlay.Visible = false;
        btnRun.Enabled = true;
        MessageBox.Show("오류 발생: " + ex.Message, "실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void ShowLogResult(string logPath, string summary)
    {
      Form frm = new Form { Text = "Pipeline Execution Report", Size = new Size(850, 650), StartPosition = FormStartPosition.CenterParent };
      TextBox txt = new TextBox
      {
        Multiline = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        ReadOnly = true,
        Font = new Font("Consolas", 10),
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.LightGray,
        Text = "=== SUMMARY ===\r\n" + summary + "\r\n\r\n=== DETAILED PROCESS LOG ===\r\n" + File.ReadAllText(logPath)
      };
      frm.Controls.Add(txt);
      frm.ShowDialog();
    }
  }
}
