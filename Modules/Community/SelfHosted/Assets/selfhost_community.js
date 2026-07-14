(function () {
  'use strict';
  if (window.selfhost_community_loaded) return;
  window.selfhost_community_loaded = true;

  var API_KEY = '4ef0d7355d9ffb5151e987764708ce96';

  function modalButton(title, action) {
    var button = $('<div class="selector" style="display:inline-block;margin:.5em .5em 0 0;padding:.7em 1em;border-radius:.7em;background:#353a43"></div>').text(title);
    button.on('click hover:enter', action);
    return button;
  }

  function openReviews(card, page, language) {
    var type = card.name ? 'tv' : 'movie';
    var url = Lampa.TMDB.api(type + '/' + card.id + '/reviews?api_key=' + API_KEY + '&language=' + encodeURIComponent(language) + '&page=' + page);
    var request = new Lampa.Reguest();
    Lampa.Loading.start();
    request.silent(url, function (data) {
      Lampa.Loading.stop();
      var reviews = data.results || [];
      if (!reviews.length && language !== 'en-US') {
        Lampa.Select.show({
          title: 'На выбранном языке рецензий нет',
          items: [{ title: 'Загрузить на английском', english: true }, { title: 'Закрыть' }],
          onSelect: function (item) { if (item.english) openReviews(card, 1, 'en-US'); },
          onBack: function () { Lampa.Controller.toggle('content'); }
        });
        return;
      }

      var root = $('<div style="padding:1.5em;max-width:1000px"></div>');
      if (!reviews.length) root.append($('<p></p>').text('Рецензий пока нет.'));
      reviews.forEach(function (review) {
        var article = $('<article style="margin-bottom:1.5em;padding-bottom:1.5em;border-bottom:1px solid #444"></article>');
        var rating = review.author_details && review.author_details.rating;
        article.append($('<h3></h3>').text((review.author || 'Автор') + (rating ? ' · ' + rating + '/10' : '')));
        if (review.created_at) article.append($('<small style="opacity:.65"></small>').text(new Date(review.created_at).toLocaleDateString()));
        var text = $('<p style="white-space:pre-wrap;line-height:1.5;max-height:9em;overflow:hidden"></p>').text(review.content || '');
        article.append(text);
        if ((review.content || '').length > 650) article.append(modalButton('Читать полностью', function () {
          var expanded = text.css('max-height') === 'none';
          text.css('max-height', expanded ? '9em' : 'none');
          this.textContent = expanded ? 'Читать полностью' : 'Свернуть';
        }));
        root.append(article);
      });
      var navigation = $('<div></div>');
      if (page > 1) navigation.append(modalButton('← Назад', function () { Lampa.Modal.close(); openReviews(card, page - 1, language); }));
      if (page < (data.total_pages || 1)) navigation.append(modalButton('Дальше →', function () { Lampa.Modal.close(); openReviews(card, page + 1, language); }));
      root.append(navigation);
      Lampa.Modal.open({ title: 'Рецензии TMDB · ' + page + '/' + (data.total_pages || 1), html: root, size: 'large', onBack: function () { Lampa.Modal.close(); } });
    }, function () {
      Lampa.Loading.stop();
      var root = $('<div style="padding:1.5em"></div>').append($('<p></p>').text('TMDB сейчас недоступен.'));
      root.append(modalButton('Повторить', function () { Lampa.Modal.close(); openReviews(card, page, language); }));
      Lampa.Modal.open({ title: 'Не удалось загрузить рецензии', html: root, onBack: function () { Lampa.Modal.close(); } });
    });
  }

  function reaction(card, value) {
    if (!window.SelfHostedAuth || !SelfHostedAuth.user()) return SelfHostedAuth.openPairing();
    var type = card.name ? 'tv' : 'movie';
    SelfHostedAuth.api('/api/v1/community/reactions', { method: 'PUT', body: JSON.stringify({ type: type, tmdbId: card.id, value: value }) })
      .then(function (data) { Lampa.Noty.show('👍 ' + data.likes + '  👎 ' + data.dislikes); })
      .catch(function (error) { Lampa.Noty.show(error.message); });
  }

  function subscribe(card) {
    if (!window.SelfHostedAuth || !SelfHostedAuth.user()) return SelfHostedAuth.openPairing();
    var type = card.name ? 'tv' : 'movie';
    SelfHostedAuth.api('/api/v1/subscriptions', { method: 'PUT', body: JSON.stringify({ type: type, tmdbId: card.id, enabled: true }) })
      .then(function () { Lampa.Noty.show('Подписка добавлена'); });
  }

  Lampa.Listener.follow('full', function (event) {
    if (event.type !== 'complite') return;
    var card = (event.data && event.data.movie) || event.object.card || event.object.item;
    if (!card || !card.id) return;
    var root = event.object.activity.render();
    var box = root.find('.full-start-new__buttons');
    if (!box.length) box = root.find('.full-start__buttons');
    if (!box.length) return;
    function button(text, action) {
      var node = $('<div class="full-start__button selector"><span></span></div>');
      node.find('span').text(text); node.on('hover:enter click', action); box.append(node);
    }
    button('Рецензии', function () { openReviews(card, 1, Lampa.Storage.get('tmdb_lang', 'ru-RU')); });
    button('👍 / 👎', function () {
      Lampa.Select.show({ title: 'Ваша реакция', items: [{ title: '👍 Нравится', v: 1 }, { title: '👎 Не нравится', v: -1 }, { title: 'Убрать реакцию', v: 0 }], onSelect: function (item) { reaction(card, item.v); }, onBack: function () { Lampa.Controller.toggle('content'); } });
    });
    button('Подписаться', function () { subscribe(card); });
  });
})();
