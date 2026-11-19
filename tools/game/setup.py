"""Dev: swap the custom battle lineup for a test (mirror match) and put the user's back afterwards.

  python setup.py mirror [design] [count]   save the current setup to docs/CustomBattleSetup.before-test.json, write a mirror match
  python setup.py restore                   put the saved setup back
"""
import json
import os
import shutil
import sys

LIVE = os.path.expandvars(r'%USERPROFILE%\AppData\LocalLow\HD\Sprocket\CustomBattleSetup.json')
SAVED = os.path.join(os.path.dirname(__file__), '..', '..', 'docs', 'CustomBattleSetup.before-test.json')
BLUEPRINTS = os.path.expandvars(r'%USERPROFILE%\Documents\My Games\Sprocket\Factions\Default\Blueprints\Vehicles')


def team(design, count, flags):
    return {
        'unitDefs': [{
            'path': os.path.join(BLUEPRINTS, design + '.blueprint'),
            'iconPath': os.path.join(BLUEPRINTS, 'Profiles', design + '.png'),
        }],
        'unitInstances': [{'Count': count}],
        'flags': flags, 'paint': '', 'condition': 0.5, 'dirt': 0.1, 'budget': [-1],
    }


def mirror(design='BBR-01B', count=10):
    if not os.path.exists(SAVED):
        shutil.copyfile(LIVE, SAVED)
    data = json.load(open(LIVE, encoding='utf-8-sig'))
    # team 1 gets one extra slot for the player's vehicle that spectate mode hides
    data['Teams'] = [team(design, count + 1, 11), team(design, count, 4)]
    json.dump(data, open(LIVE, 'w', encoding='utf-8'), indent=2)
    print('mirror', design, count)


def restore():
    if not os.path.exists(SAVED):
        print('nothing saved')
        return
    shutil.copyfile(SAVED, LIVE)
    os.remove(SAVED)
    print('restored')


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'restore':
        restore()
    else:
        mirror(*(sys.argv[2:3] or ['BBR-01B']), *([int(sys.argv[3])] if len(sys.argv) > 3 else []))
