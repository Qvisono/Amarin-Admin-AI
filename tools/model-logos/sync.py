#!/usr/bin/env python3
"""Загрузка закреплённых исходников и проверка покрытия без запуска приложения."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import date
import hashlib
import json
from pathlib import Path
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
MANIFEST = HERE / 'assets.json'
MAPPING = ROOT / 'Amarin.Core/Providers/ModelLogoCatalog.json'
SNAPSHOT = HERE / 'catalog.snapshot.json'
MAX_BYTES = 2 * 1024 * 1024
XAML_KEY = '{http://schemas.microsoft.com/winfx/2006/xaml}Key'


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def local_path(relative):
    path = (HERE / relative).resolve()
    if not path.is_relative_to(HERE) or path == HERE:
        raise ValueError(f'Путь исходника вне tools/model-logos: {relative}')
    return path


def download(url):
    if urllib.parse.urlsplit(url).scheme != 'https':
        raise ValueError(f'Ожидался HTTPS: {url}')
    request = urllib.request.Request(url, headers={'User-Agent': 'Amarin-Model-Logos/1.0'})
    with urllib.request.urlopen(request, timeout=30) as response:
        if urllib.parse.urlsplit(response.url).scheme != 'https':
            raise ValueError(f'Небезопасный redirect: {response.url}')
        data = response.read(MAX_BYTES + 1)
        content_type = response.headers.get_content_type()
    if not data or len(data) > MAX_BYTES or content_type == 'text/html':
        raise ValueError(f'Не изображение или превышен предел 2 MiB: {url}')
    return data


def source_entries(manifest):
    for asset in manifest['assets']:
        yield asset, 'file', 'sourceUrl', 'sha256'
        if asset.get('darkFile'):
            yield asset, 'darkFile', 'darkSourceUrl', 'darkSha256'


def fetch_sources(manifest, accept_changes):
    entries = list(source_entries(manifest))

    def fetch(entry):
        asset, file_field, url_field, hash_field = entry
        path = local_path(asset[file_field])
        expected = asset.get(hash_field)
        if not accept_changes and path.exists() and hashlib.sha256(path.read_bytes()).hexdigest() == expected:
            return entry, None, expected
        data = download(asset[url_field])
        digest = hashlib.sha256(data).hexdigest()
        if expected != digest and not accept_changes:
            raise ValueError(f'{asset["key"]}: источник изменился; проверьте его и используйте --accept-changes')
        return entry, data, digest

    # Сначала проверяем всю порцию, затем записываем: отказ не оставляет частично обновлённый manifest.
    with ThreadPoolExecutor(max_workers=6) as pool:
        prepared = list(pool.map(fetch, entries))
    for (asset, file_field, _, hash_field), data, digest in prepared:
        if data is not None:
            path = local_path(asset[file_field])
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        asset[hash_field] = digest
    if accept_changes:
        write_json(MANIFEST, manifest)


def resolve(model_id, mapping):
    # На снимке хранятся исходные ID OpenRouter без транспортной приставки приложения.
    model_id = model_id.strip().lower().lstrip('~').split(':', 1)[0]
    if model_id == 'auto':
        return 'Auto'
    if model_id in mapping['models']:
        return mapping['models'][model_id]
    if '/' in model_id:
        author, name = model_id.split('/', 1)
        for rule in mapping['families']:
            if rule['author'] == author and rule['contains'] in name:
                return rule['key']
        return mapping['authors'].get(author)
    for rule in mapping['unqualified']:
        if rule['contains'] in model_id:
            return rule['key']
    return None


def refresh_snapshot(mapping):
    source = read_json(SNAPSHOT)['source']
    request = urllib.request.Request(source, headers={'User-Agent': 'Amarin-Model-Logos/1.0'})
    with urllib.request.urlopen(request, timeout=30) as response:
        catalog = json.load(response)
    models = []
    missing = []
    for model in sorted(catalog['data'], key=lambda item: item['id']):
        key = resolve(model['id'], mapping)
        if key is None:
            missing.append(model['id'])
        models.append({'id': model['id'], 'key': key})
    if missing:
        raise ValueError('Новые модели без логотипа; прежний снимок сохранён:\n' + '\n'.join(missing))
    if not models:
        raise ValueError('OpenRouter вернул пустой каталог; прежний снимок сохранён')
    write_json(SNAPSHOT, {'source': source, 'capturedAt': date.today().isoformat(), 'models': models})


def verify(manifest, mapping):
    for asset, file_field, _, hash_field in source_entries(manifest):
        path = local_path(asset[file_field])
        if hashlib.sha256(path.read_bytes()).hexdigest() != asset.get(hash_field):
            raise ValueError(f'Не совпал SHA256: {path}')
        if asset['kind'] in ('bitmap', 'png'):
            staged = ROOT / 'Amarin Admin AI/Assets/ModelLogos' / path.name
            if not staged.exists() or staged.read_bytes() != path.read_bytes():
                raise ValueError(f'Обновите bitmap-ресурс через import.ps1: {staged}')
    theme_keys = []
    for theme in ('Dark', 'Light'):
        tree = ET.parse(ROOT / f'Amarin Admin AI/UI/Theme/AiLogos.{theme}.xaml')
        entries = [item.attrib[XAML_KEY] for item in tree.getroot() if XAML_KEY in item.attrib]
        if len(entries) != len(set(entries)):
            raise ValueError(f'Повторные ключи в теме {theme}')
        theme_keys.append(set(entries))
    if theme_keys[0] != theme_keys[1]:
        raise ValueError('Светлая и тёмная темы содержат разные логотипы')
    expected_keys = set(mapping['authors'].values()) | set(mapping['models'].values()) | {'Auto'}
    expected_keys.update(rule['key'] for rule in mapping['families'] + mapping['unqualified'])
    if missing := expected_keys - theme_keys[0]:
        raise ValueError('Нет ресурсов для ключей: ' + ', '.join(sorted(missing)))
    snapshot = read_json(SNAPSHOT)
    ids = [model['id'] for model in snapshot['models']]
    if not ids or len(ids) != len(set(ids)):
        raise ValueError('Снимок каталога пуст или содержит повторные ID')
    for model in snapshot['models']:
        if not model['key'] or resolve(model['id'], mapping) != model['key']:
            raise ValueError(f'Изменился логотип {model["id"]}; проверьте снимок')
    return {'capturedAt': snapshot['capturedAt'], 'models': len(snapshot['models']),
            'covered': len(snapshot['models']), 'missing': [], 'logos': len(theme_keys[0]) - 1,
            'additionalAssets': len(manifest['assets']), 'lobeCommit': manifest['lobeCommit']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--download', action='store_true', help='загрузить отсутствующие исходники по manifest')
    parser.add_argument('--accept-changes', action='store_true', help='обновить SHA256 после проверки новых источников')
    parser.add_argument('--refresh-catalog', action='store_true', help='проверить свежий каталог и обновить снимок')
    parser.add_argument('--download-only', action='store_true', help='не проверять ещё не созданный XAML')
    parser.add_argument('--report', type=Path, help='записать JSON-отчёт')
    args = parser.parse_args()
    manifest, mapping = read_json(MANIFEST), read_json(MAPPING)
    if args.accept_changes and not args.download:
        parser.error('--accept-changes требует --download')
    if args.download:
        fetch_sources(manifest, args.accept_changes)
    if args.download_only:
        return
    if args.refresh_catalog:
        refresh_snapshot(mapping)
    report = verify(manifest, mapping)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        write_json(args.report, report)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        print(f'Не удалось обновить логотипы: {error}', file=sys.stderr)
        sys.exit(1)
