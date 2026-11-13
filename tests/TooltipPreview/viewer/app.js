(function () {
  'use strict';

  const data = window.TooltipPreviewData || {};
  const site = data.site || {};
  const state = {
    docs: new Map(),
    items: new Map(),
    uidDoc: new Map(),
    query: '',
    category: '',
    restShown: 0,
    diffShown: 20,
    pageSize: 40,
    searchLimit: 80,
    loaded: new Set(),
    allRestLoaded: false,
    searchSeq: 0
  };
  const unhandled = new Set();
  let tags;
  const WIDE = /[\u1100-\u115F\u2E80-\uA4CF\uAC00-\uD7A3\uF900-\uFAFF\uFE10-\uFE19\uFE30-\uFE6F\uFF00-\uFF60\uFFE0-\uFFE6]/u;

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  function charsHtml(text) {
    let out = '';
    for (const ch of String(text || '')) out += WIDE.test(ch) ? '<span class="w">' + esc(ch) + '</span>' : esc(ch);
    return out;
  }
  function isWideChar(ch) { return WIDE.test(ch); }
  function num(value) { return Number(Number(value).toPrecision(3)).toString(); }
  function sizeCss(raw) {
    const m = /^([\d.]+)(px|%|em)?$/.exec(String(raw || '').trim().replace(/^"|"$/g, ''));
    if (!m) return null;
    const n = Number(m[1]), unit = m[2] || 'px';
    return 'font-size:' + num(unit === '%' ? n / 100 : unit === 'em' ? n : n / 16) + 'em';
  }
  function posCss(raw) {
    const value = String(raw || '').trim().replace(/^"|"$/g, '');
    if (value.endsWith('%')) return num(Number(value.slice(0, -1))) + '%';
    if (value.endsWith('em')) return num(Number(value.slice(0, -2)) * 2) + 'ch';
    return num(Number(value.replace(/px$/, '')) / 8) + 'ch';
  }
  function ratioText(value) { return num(Number(value)); }

  class TagRenderer {
    constructor(bucket) { this.unhandled = bucket; this.base = 40; this.x = 0; }
    beginLine(base) { this.base = Number(base) || 40; this.x = 0; }
    endLine() { return ''; }
    advance(units) { this.x += Number(units) || 0; }
    render(text) {
      const out = [], stack = [];
      let pos = 0;
      const re = /<(\/?[A-Za-z][^<>\n]*)>/g;
      let m;
      while ((m = re.exec(text)) !== null) {
        const chunk = text.slice(pos, m.index);
        this.x += textUnits(chunk);
        out.push(charsHtml(chunk));
        pos = re.lastIndex;
        const body = m[1];
        const name = body.split(/[=\s]/, 1)[0].toLowerCase();
        const value = body.indexOf('=') >= 0 ? body.slice(body.indexOf('=') + 1) : '';
        if (name.startsWith('/')) {
          const base = name.slice(1);
          if (stack.indexOf(base) >= 0) {
            while (stack.length) {
              const top = stack.pop();
              out.push('</span>');
              if (top === base) break;
            }
          }
          continue;
        }
        let css = null;
        if (name === 'size') css = sizeCss(value);
        else if (name === 'color') css = 'color:' + esc(value.trim().replace(/^"|"$/g, ''));
        else if (name === 'b') css = 'font-weight:bold';
        else if (name === 'i') css = 'font-style:italic';
        else if (name === 'u') css = 'text-decoration:underline';
        else if (name === 's') css = 'text-decoration:line-through';
        else if (name === 'alpha') {
          const a = value.trim().replace(/^#/, '');
          if (/^[0-9A-Fa-f]{2}$/.test(a)) css = 'opacity:' + (parseInt(a, 16) / 255).toFixed(2);
        } else if (name === 'pos') {
          // TMP 的 <pos> 是“把后续文字放到第 x 格”，不是绝对定位块。
          // 用负/正 margin 的空 span 表达游标移动，片段边界不会把它截断。
          const delta = posUnits(value, this.base) - this.x;
          if (delta > 0) out.push('<span class="pos-gap" style="width:' + num(delta) + 'ch"></span>');
          else if (delta < 0) out.push('<span class="pos-gap" style="width:0;margin-left:' + num(delta) + 'ch"></span>');
          this.x = posUnits(value, this.base);
          continue;
        } else if (name === 'sprite') {
          out.push('<span class="sprite" title="&lt;sprite&gt;">◆</span>');
          this.x += 1;
          continue;
        }
        if (css === null) {
          this.unhandled.add('<' + name + '>');
          continue;
        }
        stack.push(name);
        out.push('<span style="' + css + '">');
      }
      const tail = text.slice(pos);
      this.x += textUnits(tail);
      out.push(charsHtml(tail));
      while (stack.length) { stack.pop(); out.push('</span>'); }
      return out.join('');
    }
  }

  tags = new TagRenderer(unhandled);

  function segmentHtml(seg, spriteBars) {
    const styled = Number(seg.len == null ? 1 : seg.len) > 0;
    const css = [];
    if (styled) {
      if (seg.color) css.push('color:' + seg.color);
      if (seg.bold) css.push('font-weight:bold');
      if (seg.italic) css.push('font-style:italic');
      const deco = [];
      if (seg.underline) deco.push('underline');
      if (seg.strike) deco.push('line-through');
      if (deco.length) css.push('text-decoration:' + deco.join(' '));
      if (seg.background) css.push('background:' + seg.background);
    }
    const bar = seg.bar;
    if (bar && spriteBars && bar.fullLength !== false) {
      const length = Number(bar.length || 0) || 1;
      let ratio = bar.ratio == null ? Number(bar.fill || 0) / length : Number(bar.ratio);
      ratio = Math.max(0, Math.min(1, ratio));
      const title = (bar.fill == null ? '?' : bar.fill) + '/' + length + (bar.recorded ? ' ratio=' + ratioText(ratio) : ' (按格数)');
      tags.advance(length + 2);
      return '<span class="bar" style="width:' + (length + 2) + 'ch;background:' + esc(bar.dark || 'rgba(255,255,255,0.15)') +
        '" title="' + esc(title) + '"><span style="width:' + (ratio * 100).toFixed(2) + '%;background:' +
        esc(seg.color || '#c4c4b0') + '"></span></span>';
    }
    const inner = tags.render(seg.text || '');
    return css.length ? '<span style="' + css.join(';') + '">' + inner + '</span>' : inner;
  }

  function textUnits(text) {
    let units = 0;
    for (const ch of String(text || '').replace(/<(\/?[A-Za-z][^<>\n]*)>/g, '')) units += WIDE.test(ch) ? 2 : 1;
    return units;
  }

  function posUnits(value, base) {
    const raw = String(value || '').trim().replace(/^"|"$/g, '');
    if (raw.endsWith('%')) return Number(raw.slice(0, -1)) / 100 * base;
    if (raw.endsWith('em')) return Number(raw.slice(0, -2)) * 2;
    return Number(raw.replace(/px$/, '')) / 8;
  }

  function lineExtent(line, base) {
    let x = 0, widest = 0;
    const re = /<(\/?[A-Za-z][^<>\n]*)>/g;
    for (const seg of line.segments || []) {
      const bar = seg.bar;
      if (bar && bar.fullLength !== false) {
        x += (Number(bar.length || 0) || 1) + 2;
        widest = Math.max(widest, x);
        continue;
      }
      const text = String(seg.text || '');
      let pos = 0, m;
      re.lastIndex = 0;
      while ((m = re.exec(text)) !== null) {
        x += textUnits(text.slice(pos, m.index));
        widest = Math.max(widest, x);
        pos = re.lastIndex;
        const body = m[1];
        const name = body.split(/[=\s]/, 1)[0].toLowerCase();
        const value = body.indexOf('=') >= 0 ? body.slice(body.indexOf('=') + 1) : '';
        if (name === 'pos') x = posUnits(value, base);
        else if (name === 'sprite') x += 1;
      }
      x += textUnits(text.slice(pos));
      widest = Math.max(widest, x);
    }
    return widest;
  }

  function columnsOf(item) {
    let columns = Number(item.columns || 0);
    if (!columns) {
      const frame = item.frame || {};
      if (frame.columns) columns = Number(frame.columns);
      else {
        const layers = item.layers || {};
        columns = layers.recommendedWidth ? Math.min(Number(layers.recommendedWidth), 60) : Number(item.maxCharWidth || 40);
      }
    }
    // 公司名在上一行右对齐，不占正常文字流；预览宽度必须把这一段加回去，
    // 否则右对齐文本会顶出边框或在窄窗口里被裁掉。
    const maker = item.manufacturer || {};
    const merge = maker.inline ? Number(maker.line == null ? -1 : maker.line) : -1;
    const companyUnits = merge >= 0 ? textUnits(maker.name || '') + 1 : 0;
    let widest = columns;
    const lines = item.lines || [];
    for (let i = 0; i < lines.length; i++) {
      if (i === merge) continue;
      let units = lineExtent(lines[i], columns);
      if (merge > 0 && i === merge - 1) units += companyUnits;
      widest = Math.max(widest, units);
    }
    return widest;
  }

  function tooltipHtml(item) {
    const spriteBars = !!item.spriteBars;
    const maker = item.manufacturer || {};
    const merge = maker.inline ? Number(maker.line == null ? -1 : maker.line) : -1;
    const rows = [];
    const lines = item.lines || [];
    const columns = columnsOf(item);
    for (let i = 0; i < lines.length; i++) {
      if (i === merge) continue;
      tags.beginLine(columns);
      let body = (lines[i].segments || []).map(s => segmentHtml(s, spriteBars)).join('');
      if (merge > 0 && i === merge - 1) {
        const company = (lines[merge].segments || []).map(s => segmentHtml(s, spriteBars)).join('');
        body += '<span class="company">' + company + '</span>';
      }
      body += tags.endLine();
      rows.push('<div class="ln">' + (body || '&#8203;') + '</div>');
    }
    return '<div class="tip" style="width:' + columns + 'ch">' + rows.join('') + '</div>';
  }

  function badgesHtml(item) {
    const badges = item.badges || [];
    if (!badges.length) return '';
    return '<div class="badges">' + badges.map(b => {
      const style = 'background:' + (b.background || '#555') + ';border-color:' + (b.border || '#c0c0c0');
      return '<span class="chip" style="' + style + '" title="' + esc(b.type) + '">' + charsHtml(b.text || '') + '</span>';
    }).join('') + '</div>';
  }

  function gridHtml(item) {
    const grid = item.contentsGrid;
    if (!grid) return '';
    const cells = (grid.cells || []).map(c => '<div class="cell" title="' + esc(c.id) + '"><span>' +
      charsHtml(c.name || '') + '</span><b>' + Number(c.count || 0) + '</b></div>');
    if (grid.overflow) cells.push('<div class="cell"><b>+' + grid.overflow + '</b></div>');
    let head = grid.sealed ? '密封箱' : '内容物';
    if (grid.estimate) head += ' · ' + grid.estimate;
    return '<div class="grid"><div class="gh">' + charsHtml(head) + '</div><div class="cells">' + cells.join('') + '</div></div>';
  }

  function callsTable(calls, title) {
    const rows = calls.map(c => '<tr><td>' + esc(c.seq) + '</td><td>' + esc(c.builder) + '</td><td>' + esc(c.method) +
      '</td><td>' + esc(JSON.stringify(c.args || {})) + '</td><td>' + esc(c.origin) + '</td><td>' + esc(c.caller || '') +
      '</td><td>' + esc(c.fate || '') + '</td></tr>').join('');
    return '<details><summary>' + esc(title) + '</summary><table class="calls">' +
      '<tr><th>#</th><th>builder</th><th>方法</th><th>参数</th><th>来源</th><th>调用方</th><th>去向</th></tr>' +
      rows + '</table></details>';
  }

  function rawHtml(item) {
    // 默认不显示写入次数/来源这类调试信息；只有显式 --raw full 才在折叠区里放原始数据。
    if ((site.raw || 'none') !== 'full') return '';
    const parts = [];
    if (item.raw) parts.push('<details><summary>原始片段数据</summary><pre>' + esc(JSON.stringify(item.raw, null, 1)) + '</pre></details>');
    if ((item.calls || []).length) parts.push(callsTable(item.calls, '原版带：builder 写入调用（' + item.calls.length + '）'));
    parts.push('<details><summary>标签与内容物树</summary><pre>' +
      esc(JSON.stringify({types: item.types, tags: item.tags, inventories: item.inventories}, null, 1)) + '</pre></details>');
    return parts.join('');
  }

  function plainLines(item) {
    return (item.lines || []).map(line => (line.segments || []).map(s => s.text || '').join(''));
  }
  function itemSearch(item) {
    if (item._search) return item._search;
    item._search = [item.id, item.name].concat(plainLines(item)).join(' ').toLowerCase();
    return item._search;
  }
  function docOf(uid) { return state.uidDoc.get(uid) || {}; }
  function itemOf(uid) { return state.items.get(uid) || null; }

  function cardHtml(item, docLabel, showVariant, extra) {
    if (!item) {
      return '<div class="card missing"><div class="hd"><span class="variant">' + esc(docLabel) +
        '</span>这个变体里没有录到这件</div></div>';
    }
    const doc = docOf(item.uid);
    const meta = (doc && doc.meta) || {};
    const source = item.source || meta.source || '?';
    const check = item.check || {};
    const warn = check.segmentsMatchBuild === false ? '<span class="bad">片段与 Build 输出不一致</span>' : '';
    const label = item.label ? ' · ' + esc(item.label) : '';
    const stateText = item.state ? (site.stateNames && site.stateNames[item.state] || item.state) : '';
    const state = stateText ? '<span class="state">' + esc(stateText) + '</span>' : '';
    const variant = showVariant ? '<span class="variant">' + esc(docLabel || '') + '</span>' : '';
    const search = itemSearch(item);
    return '<div class="card ' + (extra || '') + '" data-search="' + esc(search) + '">' +
      '<div class="hd">' + variant + '<b>' + esc(item.id) + '</b> · ' + charsHtml(item.name || '') + label + state +
      '<span class="src">' + esc(source) + ' · ' + esc(meta.gameVersion || '') + ' · ' + esc(meta.language || '') +
      '</span>' + warn + '</div>' + badgesHtml(item) +
      '<div class="body">' + tooltipHtml(item) + gridHtml(item) + '</div>' + rawHtml(item) + '</div>';
  }

  function rowHeader(row) {
    let note = '';
    if (row.same === true) note = '<span class="same">各变体文字相同</span>';
    if (row.same === false) note = '<span class="differs">各变体文字不同</span>';
    return '<div class="ph">' + esc(row.id) + ' · ' + charsHtml(row.name || '') + note + '</div>';
  }

  function rowNode(row, compare) {
    const wrap = document.createElement('div');
    if (compare) {
      wrap.className = 'pair';
      wrap.dataset.search = row.id + ' ' + (row.name || '');
      const cells = (site.docs || []).map(doc => {
        const uid = row.cells && row.cells[doc.id];
        return cardHtml(uid ? itemOf(uid) : null, doc.label, true, '');
      });
      wrap.innerHTML = rowHeader(row) + '<div class="cols">' + cells.join('') + '</div>';
    } else {
      const uid = row.cells ? Object.values(row.cells)[0] : null;
      const holder = document.createElement('div');
      holder.innerHTML = cardHtml(uid ? itemOf(uid) : null, '', false, '');
      return holder.firstElementChild;
    }
    return wrap;
  }

  function renderGroups(host) {
    const h = document.createElement('h2');
    h.textContent = '典型物品';
    host.appendChild(h);
    const compare = site.mode === 'compare';
    for (const group of site.groups || []) {
      const heading = document.createElement('h3');
      heading.textContent = group.title || group.key;
      host.appendChild(heading);
      const box = document.createElement('div');
      if (!group.rows || !group.rows.length) {
        box.className = 'empty';
        box.textContent = '（快照里没有）';
        host.appendChild(box);
        continue;
      }
      if (compare) {
        for (const row of group.rows) host.appendChild(rowNode(row, true));
      } else {
        box.className = 'cards';
        for (const row of group.rows) box.appendChild(rowNode(row, false));
        host.appendChild(box);
      }
    }
  }

  function renderRest(host, restart) {
    if (!(site.rest || []).length) return;
    const target = host.querySelector('#viewport') || host;
    let box = host.querySelector('#rest');
    if (!box || restart) {
      if (box) box.remove();
      box = document.createElement('div');
      box.id = 'rest';
      const h = document.createElement('h2');
      h.textContent = '其余物品（' + site.rest.length + '）';
      box.appendChild(h);
      target.appendChild(box);
    }
    const compare = site.mode === 'compare';
    let cards = box.querySelector('#rest-cards');
    if (!cards) {
      cards = document.createElement('div');
      cards.id = 'rest-cards';
      if (!compare) cards.className = 'cards';
      box.appendChild(cards);
    }
    const shown = cards.children.length;
    const end = Math.min(site.rest.length, Math.max(shown, state.restShown));
    for (let i = shown; i < end; i++) cards.appendChild(rowNode(site.rest[i], compare));
    state.restShown = end;
    updateMore(host);
  }

  function updateMore(host) {
    const button = host.querySelector('#more');
    if (!button) return;
    const rest = site.rest || [];
    if (!rest.length || state.restShown >= rest.length || state.query || state.category) {
      button.hidden = true;
      return;
    }
    button.hidden = false;
    button.textContent = '显示其余 ' + rest.length + ' 件（再显示 ' + Math.min(state.pageSize, rest.length - state.restShown) + ' 件）';
  }

  function renderDiff(host) {
    const diff = site.diff;
    if (!diff) return;
    const h = document.createElement('h2');
    h.textContent = '差异：' + diff.changedCount + ' 件有变化，' + diff.removedCount + ' 件只在旧快照，' + diff.addedCount + ' 件只在新快照';
    host.appendChild(h);
    const list = document.createElement('div');
    list.id = 'diff-list';
    host.appendChild(list);
    renderDiffPage(list, host);
    if (diff.removedCount) {
      const p = document.createElement('p');
      p.innerHTML = '<b>只在旧快照</b>：' + esc((diff.removed || []).join(', '));
      host.appendChild(p);
    }
    if (diff.addedCount) {
      const p = document.createElement('p');
      p.innerHTML = '<b>只在新快照</b>：' + esc((diff.added || []).join(', '));
      host.appendChild(p);
    }
  }

  function renderDiffPage(list, host) {
    const diff = site.diff;
    const end = Math.min(diff.changed.length, state.diffShown);
    for (let i = list.querySelectorAll('.pair').length; i < end; i++) {
      const row = diff.changed[i];
      const oldItem = row.oldUid ? itemOf(row.oldUid) : null;
      const newItem = row.newUid ? itemOf(row.newUid) : null;
      const wrap = document.createElement('div');
      wrap.className = 'pair';
      wrap.innerHTML = '<div class="ph">' + esc(row.id) + ' · 变化：' + esc(row.kind) + '</div>' +
        '<div class="cols">' + cardHtml(oldItem, docOf(row.oldUid).label || '旧', true, 'old') +
        cardHtml(newItem, docOf(row.newUid).label || '新', true, 'new') + '</div>' +
        '<pre class="udiff">' + esc(row.udiff) + '</pre>';
      list.appendChild(wrap);
    }
    let more = host.querySelector('#diff-more');
    if (diff.changed.length > end) {
      if (!more) {
        more = document.createElement('button');
        more.id = 'diff-more';
        more.addEventListener('click', () => {
          state.diffShown += 20;
          renderDiffPage(list, host);
        });
        host.appendChild(more);
      }
      more.textContent = '显示更多差异（还剩 ' + (diff.changed.length - end) + ' 件）';
    } else if (more) {
      more.remove();
    }
    updateFooter(host);
  }

  function allRows() {
    const rows = [];
    for (const group of site.groups || []) rows.push(...(group.rows || []));
    rows.push(...(site.rest || []));
    return rows;
  }

  function renderFiltered(host, rows, title) {
    const viewport = host.querySelector('#viewport');
    viewport.innerHTML = '';
    const h = document.createElement('h2');
    h.textContent = title;
    viewport.appendChild(h);
    const status = host.querySelector('#status');
    if (!rows.length) {
      const empty = document.createElement('div');
      empty.className = 'empty';
      empty.textContent = '没有匹配的物品。';
      viewport.appendChild(empty);
      status.textContent = '0 件';
      return;
    }
    const compare = site.mode === 'compare';
    const box = document.createElement('div');
    box.className = compare ? '' : 'cards';
    for (const row of rows.slice(0, state.searchLimit)) box.appendChild(rowNode(row, compare));
    viewport.appendChild(box);
    status.textContent = rows.length + ' 件匹配' +
      (rows.length > state.searchLimit ? '，只显示前 ' + state.searchLimit + ' 件' : '');
  }

  function categoryTitle(key) {
    const found = (site.categories || []).find(category => category.key === key);
    return found ? found.title : key;
  }

  async function applyView(host) {
    const seq = ++state.searchSeq;
    const status = host.querySelector('#status');
    if (!state.query && !state.category) {
      renderAll(host);
      status.textContent = '';
      updateFooter(host);
      return;
    }
    if (!state.allRestLoaded && (site.rest || []).length) {
      status.textContent = '正在加载全部物品…';
      try {
        await ensureRest();
      } catch (error) {
        status.textContent = '加载失败：' + error.message;
        return;
      }
      if (seq !== state.searchSeq) return;
    }
    let rows = allRows();
    if (state.category) rows = rows.filter(row => (row.categories || []).indexOf(state.category) >= 0);
    if (state.query) {
      rows = rows.filter(row => {
        for (const uid of Object.values(row.cells || {})) {
          const item = uid ? itemOf(uid) : null;
          if (item && itemSearch(item).indexOf(state.query) >= 0) return true;
        }
        return false;
      });
    }
    const title = state.query ? '搜索结果' : '分类：' + categoryTitle(state.category);
    renderFiltered(host, rows, title);
    updateFooter(host);
  }

  function renderAll(host) {
    const viewport = host.querySelector('#viewport');
    viewport.innerHTML = '';
    state.restShown = 0;
    state.diffShown = 20;
    if (site.mode === 'diff') renderDiff(viewport);
    renderGroups(viewport);
    updateMore(host);
  }

  function footerHtml() {
    const approx = (site.approximations || []).map(a => '<li>' + esc(a) + '</li>').join('');
    const unknown = Array.from(unhandled).sort().join(', ') || '无';
    return '<b>与游戏逐字一致</b>：每行的文字、颜色、粗斜体/下划线/删除线、底色和折行位置都取自快照（游戏 Build 的结果），本页不重新折行。' +
      '<br><b>只是近似</b>：<ul>' + approx + '</ul>本页遇到的未处理 TMP 标签：' + esc(unknown);
  }

  function updateFooter(host) {
    const foot = host.querySelector('footer');
    if (foot) foot.innerHTML = footerHtml();
  }

  function shell() {
    const app = document.getElementById('app');
    const meta = (site.docs || []).map(d => esc(d.summary || '')).join('<br>');
    const notes = (site.notes || []).map(n => '<div class="note">' + esc(n) + '</div>').join('');
    const total = allRows().length;
    const navItems = ['<button class="cat active" data-cat="">全部 <span class="count">' + total + '</span></button>']
      .concat((site.categories || []).map(category =>
        '<button class="cat" data-cat="' + esc(category.key) + '">' + esc(category.title) +
        ' <span class="count">' + category.count + '</span></button>'))
      .join('');
    app.innerHTML = '<h1>' + esc(site.title || 'tooltip 预览') + '</h1><div class="meta">' + meta + '</div>' + notes +
      '<div id="layout"><aside id="nav"><h2>分类标签</h2><div id="cats">' + navItems + '</div></aside>' +
      '<div id="content"><div class="controls"><input id="q" placeholder="按标识、名字或正文文字过滤">' +
      '<button id="more" hidden></button><span id="status"></span></div><div id="viewport"></div></div></div>' +
      '<footer></footer>';
    const q = app.querySelector('#q');
    let timer = null;
    q.addEventListener('input', () => {
      clearTimeout(timer);
      timer = setTimeout(() => {
        state.query = q.value.trim().toLowerCase();
        applyView(app);
      }, 120);
    });
    app.querySelector('#cats').addEventListener('click', event => {
      const button = event.target.closest('button[data-cat]');
      if (!button) return;
      state.category = button.dataset.cat || '';
      for (const item of app.querySelectorAll('#cats button')) item.classList.toggle('active', item === button);
      applyView(app);
    });
    app.querySelector('#more').addEventListener('click', async () => {
      const button = app.querySelector('#more');
      button.disabled = true;
      button.textContent = '正在加载其余数据…';
      try {
        await ensureRest();
        state.restShown += state.pageSize;
        renderRest(app, false);
        updateFooter(app);
      } finally {
        button.disabled = false;
        updateMore(app);
      }
    });
    renderAll(app);
    updateFooter(app);
  }

  function loadScript(src) {
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = src;
      script.async = false;
      script.onload = resolve;
      script.onerror = () => reject(new Error('无法加载数据文件：' + src));
      document.head.appendChild(script);
    });
  }

  async function loadChunk(doc, kind) {
    const file = doc.files && doc.files[kind];
    const key = doc.id + ':' + kind;
    if (!file || state.loaded.has(key)) return;
    await loadScript(file);
    state.loaded.add(key);
    const pack = (data.docsData || {})[doc.id];
    if (!pack) return;
    for (const item of pack.items || []) {
      if (!state.items.has(item.uid)) {
        state.items.set(item.uid, item);
        state.uidDoc.set(item.uid, doc);
      }
    }
  }

  async function ensureRest() {
    if (state.allRestLoaded) return;
    for (const doc of site.docs || []) await loadChunk(doc, 'rest');
    state.allRestLoaded = true;
  }

  async function boot() {
    try {
      if (!site.schema) throw new Error('缺少 data/index.js 或站点配置。');
      document.title = site.title || document.title;
      for (const doc of site.docs || []) {
        state.docs.set(doc.id, doc);
        await loadChunk(doc, 'typical');
        if (site.mode === 'diff') await loadChunk(doc, 'rest');
      }
      if (site.mode === 'diff') state.allRestLoaded = true;
      shell();
    } catch (error) {
      document.getElementById('app').innerHTML = '<h1>预览加载失败</h1><pre>' + esc(error.message) + '</pre>' +
        '<p>请从站点根目录打开 index.html，并确认 data/ 与 assets/ 完整。</p>';
      console.error(error);
    }
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();
