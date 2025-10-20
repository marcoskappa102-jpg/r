using MMOServer.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MMOServer.Server
{
    public class SkillManager
    {
        private static SkillManager? instance;
        public static SkillManager Instance
        {
            get
            {
                if (instance == null)
                    instance = new SkillManager();
                return instance;
            }
        }

        private Dictionary<int, SkillTemplate> skillTemplates = new Dictionary<int, SkillTemplate>();
        private Dictionary<int, Dictionary<int, SkillInstance>> playerSkills = new Dictionary<int, Dictionary<int, SkillInstance>>();
        
        // ✅ CORREÇÃO #1: Buffs por personagem (não por monstro)
        private Dictionary<int, List<ActiveBuff>> activeBuffs = new Dictionary<int, List<ActiveBuff>>();
        private Random random = new Random();

        private const string CONFIG_FILE = "skills.json";
        private const string CONFIG_FOLDER = "Config";
        private int nextBuffId = 1;

        public void Initialize()
        {
            Console.WriteLine("⚡ SkillManager: Initializing...");
            LoadSkillTemplates();
            
            // ✅ CORREÇÃO #2: Carrega buffs ativos do banco
            LoadActiveBuffsFromDatabase();
            
            Console.WriteLine($"✅ SkillManager: Loaded {skillTemplates.Count} skill templates");
        }

        private void LoadSkillTemplates()
        {
            string filePath = Path.Combine(CONFIG_FOLDER, CONFIG_FILE);

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"⚠️ {CONFIG_FILE} not found!");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var config = JsonConvert.DeserializeObject<SkillConfig>(json);

                if (config?.skills != null)
                {
                    foreach (var skill in config.skills)
                    {
                        skillTemplates[skill.id] = skill;
                    }
                    Console.WriteLine($"✅ Loaded {skillTemplates.Count} skills from skills.json");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading skills.json: {ex.Message}");
            }
        }

        // ✅ CORREÇÃO #3: Carrega buffs salvos
        private void LoadActiveBuffsFromDatabase()
        {
            try
            {
                var allCharacters = DatabaseHandler.Instance.GetAllCharacters(); // Você precisa criar esse método
                
                foreach (var character in allCharacters)
                {
                    var buffs = DatabaseHandler.Instance.LoadActiveBuffs(character.id);
                    
                    if (buffs.Count > 0)
                    {
                        activeBuffs[character.id] = buffs;
                        Console.WriteLine($"✅ Loaded {buffs.Count} active buffs for character {character.id}");
                        
                        // Aplica os buffs aos stats do personagem
                        ApplyBuffsToCharacter(character);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error loading active buffs: {ex.Message}");
            }
        }

        public SkillTemplate? GetSkillTemplate(int skillId)
        {
            skillTemplates.TryGetValue(skillId, out var template);
            return template;
        }

        public bool LearnSkill(int characterId, int skillId)
        {
            var template = GetSkillTemplate(skillId);
            if (template == null)
            {
                Console.WriteLine($"❌ Skill {skillId} not found");
                return false;
            }

            var character = DatabaseHandler.Instance.GetCharacter(characterId);
            if (character == null)
            {
                Console.WriteLine($"❌ Character {characterId} not found");
                return false;
            }

            if (character.level < template.levelRequired)
            {
                Console.WriteLine($"❌ {character.nome} não tem level suficiente para aprender {template.name}");
                return false;
            }

            if (template.classRequired != 0 && GetClassId(character.classe) != template.classRequired)
            {
                Console.WriteLine($"❌ {template.name} não é compatível com a classe {character.classe}");
                return false;
            }

            if (!playerSkills.ContainsKey(characterId))
            {
                playerSkills[characterId] = new Dictionary<int, SkillInstance>();
            }

            if (playerSkills[characterId].ContainsKey(skillId))
            {
                Console.WriteLine($"⚠️ {character.nome} já aprendeu {template.name}");
                return false;
            }

            var skillInstance = new SkillInstance
            {
                skillId = skillId,
                characterId = characterId,
                level = 1,
                isLearned = true,
                lastCastTime = DateTime.MinValue
            };

            playerSkills[characterId][skillId] = skillInstance;
            DatabaseHandler.Instance.SaveCharacterSkill(skillInstance);
            Console.WriteLine($"✅ {character.nome} aprendeu {template.name}!");
            return true;
        }

        public List<SkillTemplate> GetLearnableSkills(Character character)
        {
            var learnable = new List<SkillTemplate>();
            int classId = GetClassId(character.classe);

            foreach (var skill in skillTemplates.Values)
            {
                if (playerSkills.ContainsKey(character.id) && 
                    playerSkills[character.id].ContainsKey(skill.id))
                {
                    continue;
                }

                if (character.level < skill.levelRequired)
                    continue;

                if (skill.classRequired != 0 && skill.classRequired != classId)
                    continue;

                learnable.Add(skill);
            }

            return learnable;
        }

        public SkillCastResult CastSkill(int characterId, int skillId, int targetId, float currentTime)
        {
            var character = DatabaseHandler.Instance.GetCharacter(characterId);
            if (character == null)
            {
                return CreateFailResult(skillId, "CASTER_NOT_FOUND");
            }

            var template = GetSkillTemplate(skillId);
            if (template == null)
            {
                return CreateFailResult(skillId, "SKILL_NOT_FOUND");
            }

            if (character.isDead)
            {
                return CreateFailResult(skillId, "CASTER_DEAD");
            }

            if (!HasSkillLearned(characterId, skillId))
            {
                return CreateFailResult(skillId, "SKILL_NOT_LEARNED");
            }

            // ✅ CORREÇÃO #4: Valida mana ANTES de gastar
            if (character.mana < template.manaCost)
            {
                return CreateFailResult(skillId, "INSUFFICIENT_MANA");
            }

            if (character.health <= template.healthCost)
            {
                return CreateFailResult(skillId, "INSUFFICIENT_HEALTH");
            }

            var skillInstance = playerSkills[characterId][skillId];
            float timeSinceLast = (float)(DateTime.UtcNow - skillInstance.lastCastTime).TotalSeconds;

            if (timeSinceLast < template.cooldown)
            {
                return CreateFailResult(skillId, "ON_COOLDOWN");
            }

            // ✅ Validação de alvo
            if (template.targetType == "single" || template.targetType == "aoe")
            {
                if (template.skillType == "heal")
                {
                    // Skills de cura podem ter target = 0 (self)
                    if (targetId != 0 && targetId != characterId)
                    {
                        // Se targetId for outro player, valida se existe
                        var targetPlayer = PlayerManager.Instance.GetPlayer(targetId.ToString());
                        if (targetPlayer == null)
                        {
                            return CreateFailResult(skillId, "TARGET_NOT_FOUND");
                        }
                    }
                }
                else
                {
                    // Skills de ataque precisam de monstro válido
                    var target = MonsterManager.Instance.GetMonster(targetId);
                    if (target == null || !target.isAlive)
                    {
                        return CreateFailResult(skillId, "TARGET_NOT_FOUND");
                    }

                    float distance = CombatManager.Instance.GetDistance(character.position, target.position);
                    if (distance > template.range)
                    {
                        return CreateFailResult(skillId, "OUT_OF_RANGE");
                    }
                }
            }

            // ✅ CORREÇÃO #5: Gasta recursos DEPOIS da validação
            character.mana -= template.manaCost;
            character.health -= template.healthCost;

            if (character.health < 0)
                character.health = 0;

            skillInstance.lastCastTime = DateTime.UtcNow;

            var result = new SkillCastResult
            {
                casterId = character.id,
                casterName = character.nome,
                casterType = "player",
                skillId = skillId,
                skillName = template.name,
                success = true,
                castTime = template.castTime,
                targetResults = new List<SkillTargetResult>()
            };

            // ✅ CORREÇÃO #6: Determina alvos corretamente por tipo de skill
            if (template.skillType == "attack")
            {
                List<MonsterInstance> targets = DetermineTargets(character, template, targetId);
                foreach (var target in targets)
                {
                    var targetResult = ApplyDamageSkill(character, template, target);
                    result.targetResults.Add(targetResult);
                }
            }
            else if (template.skillType == "heal")
            {
                var healResult = ApplyHealSkill(character, template, targetId);
                result.targetResults.Add(healResult);
            }
            else if (template.skillType == "buff" || template.skillType == "debuff")
            {
                var buffResult = ApplyBuffSkill(character, template, targetId);
                result.targetResults.Add(buffResult);
            }

            DatabaseHandler.Instance.UpdateCharacter(character);
            return result;
        }

        private List<MonsterInstance> DetermineTargets(Character caster, SkillTemplate template, int primaryTargetId)
        {
            var targets = new List<MonsterInstance>();

            if (template.targetType == "single")
            {
                var target = MonsterManager.Instance.GetMonster(primaryTargetId);
                if (target != null && target.isAlive)
                {
                    targets.Add(target);
                }
            }
            else if (template.targetType == "aoe")
            {
                var primaryTarget = MonsterManager.Instance.GetMonster(primaryTargetId);
                if (primaryTarget == null || !primaryTarget.isAlive)
                    return targets;

                var allMonsters = MonsterManager.Instance.GetAliveMonsters();
                int hitCount = 0;

                foreach (var monster in allMonsters)
                {
                    float distance = CombatManager.Instance.GetDistance(primaryTarget.position, monster.position);

                    if (distance <= template.aoeRadius)
                    {
                        targets.Add(monster);
                        hitCount++;

                        if (template.maxTargets > 0 && hitCount >= template.maxTargets)
                        {
                            break;
                        }
                    }
                }
            }

            return targets;
        }

        private SkillTargetResult ApplyDamageSkill(Character caster, SkillTemplate template, MonsterInstance target)
        {
            var result = new SkillTargetResult
            {
                targetId = target.id,
                targetName = target.template.name,
                targetType = "monster"
            };

            if (template.canMiss)
            {
                int casterHit = 175 + caster.dexterity + caster.level;
                int targetFlee = 100 + target.template.level + target.template.defense;
                float hitChance = 0.80f + ((casterHit - targetFlee) / 100f);
                hitChance = Math.Clamp(hitChance, 0.30f, 0.95f);

                if (random.NextDouble() > hitChance)
                {
                    result.isMiss = true;
                    Console.WriteLine($"❌ {caster.nome} MISSED {target.template.name} with {template.name}");
                    return result;
                }
            }

            int baseDamage = template.baseDamage;
            baseDamage += (int)(caster.strength * template.strScale);
            baseDamage += (int)(caster.intelligence * template.intScale);
            baseDamage += (int)(caster.dexterity * template.dexScale);
            baseDamage += (int)(caster.vitality * template.vitScale);

            baseDamage = (int)(baseDamage * (1.0f + (caster.level - 1) * template.levelScale * 0.1f));

            float variance = 0.95f + ((float)random.NextDouble() * 0.10f);
            int damage = (int)(baseDamage * template.damageMultiplier * variance);

            damage = Math.Clamp(damage, template.minDamage, template.maxDamage);

            bool isCrit = false;
            if (template.criticalChance > 0)
            {
                if (random.Next(100) < template.criticalChance)
                {
                    damage = (int)(damage * 1.5f);
                    isCrit = true;
                }
            }

            int guaranteedDamage = (int)(damage * 0.1f);
            int defensibleDamage = damage - guaranteedDamage;
            float defReduction = 1.0f - (target.template.defense / (float)(target.template.defense + 100));
            defReduction = Math.Max(defReduction, 0.1f);

            int finalDamage = guaranteedDamage + (int)(defensibleDamage * defReduction);
            finalDamage = Math.Max(finalDamage, 1);

            int actualDamage = target.TakeDamage(finalDamage);

            result.damage = actualDamage;
            result.isCritical = isCrit;
            result.remainingHealth = target.currentHealth;
            result.died = !target.isAlive;

            Console.WriteLine($"⚡ {caster.nome} cast {template.name} on {target.template.name}: {actualDamage} dmg{(isCrit ? " CRIT!" : "")}");

            if (result.died)
            {
                Console.WriteLine($"💀 {target.template.name} died from {template.name}!");
            }

            try
            {
                DatabaseHandler.Instance.LogSkillCast(caster.id, template.id, target.id, 
                    target.template.name, true, actualDamage, 0, isCrit, false);
            }
            catch { }

            return result;
        }

        // ✅ CORREÇÃO #7: Renomeado e corrigido para curar o ALVO, não o caster
        private SkillTargetResult ApplyHealSkill(Character caster, SkillTemplate template, int targetId)
        {
            // Se targetId = 0 ou = characterId, cura a si mesmo
            Character targetCharacter = caster;
            
            if (targetId != 0 && targetId != caster.id)
            {
                var targetPlayer = PlayerManager.Instance.GetPlayer(targetId.ToString());
                if (targetPlayer != null)
                {
                    targetCharacter = targetPlayer.character;
                }
            }

            var result = new SkillTargetResult
            {
                targetId = targetCharacter.id,
                targetName = targetCharacter.nome,
                targetType = "player",
                isMiss = false
            };

            int healAmount = template.effectValue;
            healAmount += (int)(caster.intelligence * template.intScale);
            healAmount += (int)(caster.vitality * template.vitScale);

            float variance = 0.95f + ((float)random.NextDouble() * 0.10f);
            healAmount = (int)(healAmount * variance);

            int oldHealth = targetCharacter.health;
            targetCharacter.health = Math.Min(targetCharacter.health + healAmount, targetCharacter.maxHealth);
            int actualHeal = targetCharacter.health - oldHealth;

            result.heal = actualHeal;
            result.remainingHealth = targetCharacter.health;

            Console.WriteLine($"💚 {caster.nome} cast {template.name} on {targetCharacter.nome}: healed {actualHeal} HP ({oldHealth} -> {targetCharacter.health})");

            // Salva o personagem curado
            DatabaseHandler.Instance.UpdateCharacter(targetCharacter);

            try
            {
                DatabaseHandler.Instance.LogSkillCast(caster.id, template.id, targetCharacter.id, 
                    "player", true, 0, actualHeal, false, false);
            }
            catch { }

            return result;
        }

        // ✅ CORREÇÃO #8: Buffs aplicam em personagens, não monstros
        private SkillTargetResult ApplyBuffSkill(Character caster, SkillTemplate template, int targetId)
        {
            Character targetCharacter = caster;
            
            if (targetId != 0 && targetId != caster.id)
            {
                var targetPlayer = PlayerManager.Instance.GetPlayer(targetId.ToString());
                if (targetPlayer != null)
                {
                    targetCharacter = targetPlayer.character;
                }
            }

            var result = new SkillTargetResult
            {
                targetId = targetCharacter.id,
                targetName = targetCharacter.nome,
                targetType = "player",
                appliedBuffs = new List<ActiveBuff>()
            };

            var buff = new ActiveBuff
            {
                buffId = nextBuffId++,
                buffName = template.name,
                skillName = template.name,
                skillId = template.id,
                casterId = caster.id,
                buffType = template.skillType,
                effectType = template.effectType,
                statBoost = template.effectValue,
                affectedStat = template.effectTarget,
                remainingDuration = template.effectDuration,
                applicationTime = DateTime.UtcNow,
                isActive = true
            };

            if (!activeBuffs.ContainsKey(targetCharacter.id))
            {
                activeBuffs[targetCharacter.id] = new List<ActiveBuff>();
            }

            activeBuffs[targetCharacter.id].Add(buff);
            result.appliedBuffs.Add(buff);

            // ✅ CORREÇÃO #9: Aplica buff aos stats do personagem
            ApplyBuffToCharacter(targetCharacter, buff);

            Console.WriteLine($"✨ {template.name} applied to {targetCharacter.nome} for {template.effectDuration}s");

            try
            {
                DatabaseHandler.Instance.SaveActiveBuff(targetCharacter.id, buff);
                DatabaseHandler.Instance.UpdateCharacter(targetCharacter);
            }
            catch { }

            return result;
        }

        // ✅ CORREÇÃO #10: Método para aplicar buff aos stats
        private void ApplyBuffToCharacter(Character character, ActiveBuff buff)
        {
            switch (buff.affectedStat.ToLower())
            {
                case "str":
                case "strength":
                    character.strength += buff.statBoost;
                    break;
                case "int":
                case "intelligence":
                    character.intelligence += buff.statBoost;
                    break;
                case "dex":
                case "dexterity":
                    character.dexterity += buff.statBoost;
                    break;
                case "vit":
                case "vitality":
                    character.vitality += buff.statBoost;
                    break;
                case "def":
                case "defense":
                    character.defense += buff.statBoost;
                    break;
                case "atk":
                case "attackpower":
                    character.attackPower += buff.statBoost;
                    break;
                case "matk":
                case "magicpower":
                    character.magicPower += buff.statBoost;
                    break;
            }

            character.RecalculateStats();
        }

        // ✅ CORREÇÃO #11: Método para remover buff dos stats
        private void RemoveBuffFromCharacter(Character character, ActiveBuff buff)
        {
            switch (buff.affectedStat.ToLower())
            {
                case "str":
                case "strength":
                    character.strength -= buff.statBoost;
                    break;
                case "int":
                case "intelligence":
                    character.intelligence -= buff.statBoost;
                    break;
                case "dex":
                case "dexterity":
                    character.dexterity -= buff.statBoost;
                    break;
                case "vit":
                case "vitality":
                    character.vitality -= buff.statBoost;
                    break;
                case "def":
                case "defense":
                    character.defense -= buff.statBoost;
                    break;
                case "atk":
                case "attackpower":
                    character.attackPower -= buff.statBoost;
                    break;
                case "matk":
                case "magicpower":
                    character.magicPower -= buff.statBoost;
                    break;
            }

            character.RecalculateStats();
        }

        // ✅ CORREÇÃO #12: Aplica todos os buffs ao personagem ao carregar
        private void ApplyBuffsToCharacter(Character character)
        {
            if (!activeBuffs.ContainsKey(character.id))
                return;

            foreach (var buff in activeBuffs[character.id])
            {
                if (buff.isActive)
                {
                    ApplyBuffToCharacter(character, buff);
                }
            }
        }

        // ✅ CORREÇÃO #13: UpdateBuffs agora remove buffs dos stats e salva no banco
        public void UpdateBuffs(float deltaTime)
        {
            var characterIds = activeBuffs.Keys.ToList();

            foreach (var characterId in characterIds)
            {
                var buffs = activeBuffs[characterId];
                var expiredBuffs = new List<ActiveBuff>();

                for (int i = buffs.Count - 1; i >= 0; i--)
                {
                    var buff = buffs[i];
                    buff.remainingDuration -= deltaTime;

                    if (buff.remainingDuration <= 0)
                    {
                        buff.isActive = false;
                        expiredBuffs.Add(buff);
                        
                        // Remove buff dos stats do personagem
                        var character = DatabaseHandler.Instance.GetCharacter(characterId);
                        if (character != null)
                        {
                            RemoveBuffFromCharacter(character, buff);
                            DatabaseHandler.Instance.UpdateCharacter(character);
                            Console.WriteLine($"⏱️ Buff {buff.buffName} expired on {character.nome}");
                        }

                        // Atualiza no banco
                        try
                        {
                            DatabaseHandler.Instance.UpdateBuffExpiration(buff.buffId, 0);
                        }
                        catch { }

                        buffs.RemoveAt(i);
                    }
                    else
                    {
                        // Atualiza duração no banco periodicamente (a cada 5s)
                        if ((int)buff.remainingDuration % 5 == 0)
                        {
                            try
                            {
                                DatabaseHandler.Instance.UpdateBuffExpiration(buff.buffId, buff.remainingDuration);
                            }
                            catch { }
                        }
                    }
                }

                if (buffs.Count == 0)
                {
                    activeBuffs.Remove(characterId);
                }
            }
        }

        public List<ActiveBuff> GetActiveBuffs(int characterId)
        {
            activeBuffs.TryGetValue(characterId, out var buffs);
            return buffs ?? new List<ActiveBuff>();
        }

        private bool HasSkillLearned(int characterId, int skillId)
        {
            return playerSkills.ContainsKey(characterId) && 
                   playerSkills[characterId].ContainsKey(skillId);
        }

        private SkillCastResult CreateFailResult(int skillId, string reason)
        {
            return new SkillCastResult
            {
                skillId = skillId,
                success = false,
                failReason = reason,
                targetResults = new List<SkillTargetResult>()
            };
        }

        private int GetClassId(string className)
        {
            return className switch
            {
                "Guerreiro" => 1,
                "Mago" => 2,
                "Arqueiro" => 3,
                "Clerigo" => 4,
                _ => 0
            };
        }

        public Dictionary<int, SkillInstance> GetPlayerSkills(int characterId)
        {
            if (playerSkills.ContainsKey(characterId))
                return playerSkills[characterId];

            return new Dictionary<int, SkillInstance>();
        }

        public void LoadPlayerSkills(int characterId)
        {
            if (!playerSkills.ContainsKey(characterId))
            {
                var skills = DatabaseHandler.Instance.LoadCharacterSkills(characterId);
                playerSkills[characterId] = new Dictionary<int, SkillInstance>();
                
                foreach (var skill in skills)
                {
                    playerSkills[characterId][skill.skillId] = skill;
                }
            }
        }

        public void ReloadConfigs()
        {
            Console.WriteLine("🔄 Reloading skill configurations...");
            skillTemplates.Clear();
            LoadSkillTemplates();
            Console.WriteLine("✅ Skill configurations reloaded!");
        }
    }
}