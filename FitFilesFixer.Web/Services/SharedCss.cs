namespace FitFilesFixer.Web.Services;

public static class SharedCss
{
    public static string Footer(string lang) => lang == "uk"
        ? @"<div class='donate-footer'>
  <a class='donate-banner' href='https://defensivewave.org/' target='_blank' rel='noopener'>
    <div class='donate-banner-left'>
      <div class='donate-flag'>🇺🇦</div>
      <div class='donate-text'>
        <strong>Підтримай захист України</strong>
        <span>Задонать на українську армію через Defensive Wave</span>
      </div>
    </div>
    <div class='donate-btn'>Задонатити →</div>
  </a>
</div>"
        : @"<div class='donate-footer'>
  <a class='donate-banner' href='https://defensivewave.org/' target='_blank' rel='noopener'>
    <div class='donate-banner-left'>
      <div class='donate-flag'>🇺🇦</div>
      <div class='donate-text'>
        <strong>Support Ukraine's Defence</strong>
        <span>Donate to the Ukrainian army via Defensive Wave</span>
      </div>
    </div>
    <div class='donate-btn'>Donate now →</div>
  </a>
</div>";

    public const string Css = @"  body { font-family: sans-serif; padding: 32px 24px; max-width: 760px; margin: 0 auto; color: #111; background: #fff; }
  h1 { font-size: 20px; font-weight: 500; margin: 0 0 4px; }
  .sub { font-size: 13px; color: #666; margin: 0 0 28px; }
  .section-label { font-size: 11px; font-weight: 500; color: #888; text-transform: uppercase; letter-spacing: 0.05em; margin: 0 0 8px; }
  hr { border: none; border-top: 1px solid #eee; margin: 20px 0; }
  .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin: 0 0 24px; }
  .kpi { background: #f5f5f5; border-radius: 8px; padding: 12px 16px; }
  .kpi .val { font-size: 22px; font-weight: 500; }
  .kpi .val.good { color: #1a7f4b; }
  .kpi .lbl { font-size: 12px; color: #666; margin-top: 2px; }
  .drop-row { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 24px; }
  .drop-badge { background: #fdecea; color: #a32d2d; border-radius: 6px; padding: 5px 12px; font-size: 13px; }
  .tip { background: #e8f0fb; border: 1px solid #b5d4f4; border-radius: 10px; padding: 14px 18px; display: flex; gap: 12px; align-items: flex-start; margin-bottom: 24px; }
  .tip-text { font-size: 14px; color: #185fa5; line-height: 1.6; }
  .tip-text a { color: #185fa5; font-weight: 500; }
  .actions { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
  .btn-primary { background: #111; color: #fff; border: none; border-radius: 8px; padding: 10px 20px; font-size: 14px; font-weight: 500; cursor: pointer; text-decoration: none; display: inline-block; }
  .btn-primary:hover { opacity: 0.85; }
  .btn-secondary { background: transparent; color: #555; border: 1px solid #ccc; border-radius: 8px; padding: 10px 20px; font-size: 14px; text-decoration: none; display: inline-block; }
  .page-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 24px; }
  .page-header-left { display: flex; align-items: center; gap: 10px; }
  .icon-wrap { width: 32px; height: 32px; border-radius: 50%; background: #f5f5f5; border: 1px solid #ddd; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  .lang-toggle { display: flex; gap: 4px; }
  .lang-btn { font-size: 12px; padding: 4px 10px; border-radius: 6px; border: 1px solid #ddd; background: #fff; color: #555; cursor: pointer; text-decoration: none; }
  .lang-btn.active { background: #111; color: #fff; border-color: #111; }
  .success-header { display: flex; align-items: center; gap: 10px; margin-bottom: 24px; }
  .icon-ok { width: 32px; height: 32px; border-radius: 50%; background: #d4edda; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  label { font-size: 13px; color: #666; display: block; margin-bottom: 6px; }
  .field { margin-bottom: 20px; }
  input[type=text] { border: 1px solid #ccc; border-radius: 8px; padding: 8px 12px; font-size: 14px; outline: none; }
  input[type=text]:focus { border-color: #999; }
  input[type=number] { border: 1px solid #ccc; border-radius: 8px; padding: 8px 12px; font-size: 14px; outline: none; width: 100px; }
  input[type=number]:focus { border-color: #999; }
  .threshold-row { display: flex; align-items: center; gap: 10px; }
  .threshold-unit { font-size: 14px; color: #444; }
  .threshold-hint { font-size: 12px; color: #999; }
  .upload-area { border: 1px dashed #bbb; border-radius: 12px; padding: 24px; text-align: center; cursor: pointer; background: #fafafa; transition: background 0.15s; }
  .upload-area:hover { background: #f0f0f0; }
  .file-hint { font-size: 12px; color: #888; margin-top: 6px; }
  .captcha-row { display: flex; align-items: center; gap: 10px; }
  .captcha-q { font-size: 15px; font-weight: 500; background: #f5f5f5; border: 1px solid #ddd; border-radius: 8px; padding: 8px 14px; white-space: nowrap; }
  .captcha-row input { max-width: 80px; }
  .err { font-size: 13px; color: #c0392b; margin-top: 6px; display: none; }
  #file-name { margin-top: 8px; font-size: 13px; font-weight: 500; color: #185fa5; display: none; }
  table { border-collapse: collapse; width: 100%; margin-top: 10px; font-size: 0.9em; }
  th, td { border: 1px solid #ccc; padding: 6px 10px; text-align: left; }
  th { background: #f0f0f0; }
  tr:nth-child(even) { background: #fafafa; }
  .stat-kpi { display: inline-block; background:#f5f5f5; border:1px solid #ddd; border-radius:6px; padding:12px 24px; margin:6px; text-align:center; }
  .stat-kpi .val { font-size:2em; font-weight:bold; }
  .stat-kpi .lbl { font-size:0.8em; color:#666; }
  .donate-footer { margin-top: 48px; padding-top: 20px; border-top: 1px solid #eee; }
  .donate-banner { display: flex; align-items: center; justify-content: space-between; gap: 16px; background: #0057b8; border-radius: 10px; padding: 16px 20px; text-decoration: none; flex-wrap: wrap; }
  .donate-banner:hover { opacity: 0.93; }
  .donate-banner-left { display: flex; align-items: center; gap: 12px; }
  .donate-flag { font-size: 24px; flex-shrink: 0; }
  .donate-text { color: #fff; }
  .donate-text strong { display: block; font-size: 15px; font-weight: 500; }
  .donate-text span { font-size: 13px; opacity: 0.85; }
  .donate-btn { background: #ffd700; color: #0057b8; font-size: 13px; font-weight: 500; border-radius: 6px; padding: 8px 18px; white-space: nowrap; flex-shrink: 0; }
  .city-wrap { position: relative; display: inline-block; width: 280px; }
  .city-wrap input[type=text] { width: 100%; box-sizing: border-box; }
  .city-suggestions { position: absolute; top: 100%; left: 0; right: 0; background: #fff; border: 1px solid #ccc; border-radius: 8px; box-shadow: 0 4px 12px rgba(0,0,0,.1); z-index: 100; display: none; max-height: 220px; overflow-y: auto; margin-top: 2px; }
  .city-item { padding: 8px 12px; font-size: 14px; cursor: pointer; }
  .city-item:hover { background: #f5f5f5; }
  .field-hint { font-size: 12px; color: #999; margin-top: 5px; }
  #map { width: 100%; height: 420px; border-radius: 12px; border: 1px solid #ddd; margin-bottom: 8px; }
  .map-legend { display: flex; gap: 16px; margin-bottom: 24px; }
  .legend-item { display: flex; align-items: center; gap: 6px; font-size: 13px; color: #444; }
  .legend-dot { width: 12px; height: 12px; border-radius: 50%; flex-shrink: 0; }
  .legend-dot.ok { background: #2563eb; }
  .legend-dot.fixed { background: #e53e3e; }";
}
